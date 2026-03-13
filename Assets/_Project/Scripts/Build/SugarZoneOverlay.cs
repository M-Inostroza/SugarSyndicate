using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Runtime overlay that drops a simple SpriteRenderer on every sugar zone cell.
/// </summary>
public class SugarZoneOverlay : MonoBehaviour
{
    [Header("Setup")]
    [SerializeField] GridService grid;
    [SerializeField] bool matchGridLayer = true;

    [Header("Visuals")]
    [SerializeField] Color outerColor = new Color(1f, 1f, 1f, 0.35f);
    [SerializeField] Color centerColor = new Color(1f, 1f, 1f, 0.7f);
    [Tooltip("If true, color blends from outer to center based on sugar amount.")]
    [SerializeField] bool colorByEfficiency = true;
    [SerializeField] bool highlightCenter = true;
    [SerializeField] Sprite[] depositSprites = new Sprite[0];
    [SerializeField] bool randomizeSpritePerCell = true;
    [SerializeField] bool randomizeRotationPerCell = true;
    [SerializeField] int visualSeed = 1337;
    [SerializeField, Min(0f)] float padding = 0.05f;
    [SerializeField] float zOffset = 0.05f; // base world Z for overlays
    [SerializeField] string sortingLayerName = "Default";
    [SerializeField] int sortingOrder = 1000;
    [SerializeField] bool visibleOnStart = false;

    static Sprite quadSprite; // 1x1 white full-rect sprite
    readonly List<SpriteRenderer> sprites = new();
    int sortingLayerId;

    public int LastQuadCount => sprites.Count;

    public static SugarZoneOverlay FindOrCreate(GridService gridService)
    {
        if (gridService == null) return null;
        var overlay = gridService.GetComponent<SugarZoneOverlay>();
        if (overlay == null) overlay = gridService.gameObject.AddComponent<SugarZoneOverlay>();
        if (overlay.grid == null) overlay.grid = gridService;
        overlay.Rebuild();
        overlay.SetVisible(false);
        return overlay;
    }

    void Awake()
    {
        sortingLayerId = SortingLayer.NameToID(sortingLayerName);
        if (sortingLayerId == 0) sortingLayerId = SortingLayer.NameToID("Default");
        if (grid == null) grid = GridService.Instance != null ? GridService.Instance : GetComponent<GridService>();
        if (matchGridLayer && grid != null) gameObject.layer = grid.gameObject.layer;
        EnsureSprite();
    }

    void OnValidate()
    {
        sortingLayerId = SortingLayer.NameToID(sortingLayerName);
        if (sortingLayerId == 0) sortingLayerId = SortingLayer.NameToID("Default");
        TryAutoAssignDepositSprites();
    }

    void Start()
    {
        Rebuild();
        SetVisible(visibleOnStart);
    }

    public void Show()
    {
        Rebuild();
        SetVisible(true);
    }

    public void Hide() => SetVisible(false);

    public void SetVisible(bool visible)
    {
        foreach (var sr in sprites) if (sr != null) sr.enabled = visible;
    }

    public void Rebuild()
    {
        ClearSprites();
        if (grid == null) grid = GridService.Instance ?? FindAnyObjectByType<GridService>();
        if (grid == null) return;
        if (matchGridLayer) gameObject.layer = grid.gameObject.layer;

        var size = grid.GridSize;
        float cellSize = grid.CellSize;
        float maxPad = Mathf.Max(0f, cellSize * 0.5f - 0.001f);
        float pad = Mathf.Clamp(padding, 0f, maxPad);
        float final = Mathf.Max(0.001f, cellSize - pad * 2f);
        float z = ResolveZ();
        float centerEff = grid.SugarCenterEfficiency;
        float outerEff = grid.SugarOuterEfficiency;

        for (int y = 0; y < size.y; y++)
        {
            for (int x = 0; x < size.x; x++)
            {
                var c = new Vector2Int(x, y);
                if (!grid.IsSugar(c)) continue;
                var world = grid.CellToWorld(c, z);
                var color = outerColor;
                float eff = grid.GetSugarEfficiency(c);
                if (colorByEfficiency)
                {
                    float t = Mathf.Abs(centerEff - outerEff) > 0.0001f
                        ? Mathf.InverseLerp(outerEff, centerEff, eff)
                        : 1f;
                    color = Color.Lerp(outerColor, centerColor, Mathf.Clamp01(t));
                }
                else if (highlightCenter && centerEff > outerEff && eff >= centerEff - 0.0001f)
                {
                    color = centerColor;
                }
                AddSprite(c, world, final, color);
            }
        }
    }

    void AddSprite(Vector2Int cell, Vector3 worldPos, float size, Color color)
    {
        EnsureSprite();
        var go = new GameObject("SugarZoneOverlay");
        go.transform.SetParent(transform, false);
        go.layer = gameObject.layer;
        go.transform.position = worldPos;

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = GetSpriteForCell(cell);
        sr.color = color;
        sr.sortingLayerID = sortingLayerId;
        sr.sortingOrder = sortingOrder;
        sr.drawMode = SpriteDrawMode.Simple;
        go.transform.localRotation = Quaternion.Euler(0f, 0f, GetRotationForCell(cell));
        go.transform.localScale = GetScaleForSprite(sr.sprite, size);
        sprites.Add(sr);
    }

    void ClearSprites()
    {
        foreach (var sr in sprites)
        {
            if (sr == null) continue;
            if (Application.isPlaying) Destroy(sr.gameObject);
            else DestroyImmediate(sr.gameObject);
        }
        sprites.Clear();
    }

    void EnsureSprite()
    {
        if (quadSprite != null) return;
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "SugarOverlayTex" };
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        quadSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        quadSprite.name = "SugarOverlaySprite";
    }

    float ResolveZ()
    {
        float z = zOffset;
        var cam = Camera.main;
        if (cam != null)
        {
            // Ensure we are beyond the camera's near clip to avoid being culled when near clip is large
            z = Mathf.Max(z, cam.nearClipPlane + 0.05f);
        }
        return z;
    }

    Sprite GetSpriteForCell(Vector2Int cell)
    {
        if (depositSprites == null || depositSprites.Length == 0)
            return quadSprite;
        if (!randomizeSpritePerCell)
            return depositSprites[0] != null ? depositSprites[0] : quadSprite;

        int hash = GetCellVisualHash(cell);
        for (int i = 0; i < depositSprites.Length; i++)
        {
            var sprite = depositSprites[(hash + i) % depositSprites.Length];
            if (sprite != null)
                return sprite;
        }
        return quadSprite;
    }

    float GetRotationForCell(Vector2Int cell)
    {
        if (!randomizeRotationPerCell) return 0f;
        int hash = GetCellVisualHash(cell);
        int rotationIndex = ((hash / 31) & 3);
        return rotationIndex * 90f;
    }

    Vector3 GetScaleForSprite(Sprite sprite, float targetSize)
    {
        sprite ??= quadSprite;
        if (sprite == null) return Vector3.one;

        var bounds = sprite.bounds.size;
        float width = Mathf.Max(0.0001f, bounds.x);
        float height = Mathf.Max(0.0001f, bounds.y);
        float scale = targetSize / Mathf.Max(width, height);
        return new Vector3(scale, scale, 1f);
    }

    int GetCellVisualHash(Vector2Int cell)
    {
        unchecked
        {
            int hash = visualSeed;
            hash = (hash * 397) ^ cell.x;
            hash = (hash * 397) ^ cell.y;
            return hash & int.MaxValue;
        }
    }

    void TryAutoAssignDepositSprites()
    {
#if UNITY_EDITOR
        bool needsAssignment = depositSprites == null || depositSprites.Length == 0;
        if (!needsAssignment)
        {
            for (int i = 0; i < depositSprites.Length; i++)
            {
                if (depositSprites[i] == null)
                {
                    needsAssignment = true;
                    break;
                }
            }
        }

        if (!needsAssignment) return;

        var sprites = new List<Sprite>(2);
        AddEditorSpriteIfFound(sprites, "Assets/_Project/Art/Sugar depo v1.png");
        AddEditorSpriteIfFound(sprites, "Assets/_Project/Art/Sugar depo v1-2.png");
        if (sprites.Count <= 0) return;

        depositSprites = sprites.ToArray();
        EditorUtility.SetDirty(this);
#endif
    }

#if UNITY_EDITOR
    static void AddEditorSpriteIfFound(List<Sprite> destination, string assetPath)
    {
        if (destination == null || string.IsNullOrWhiteSpace(assetPath)) return;
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
        if (sprite != null)
            destination.Add(sprite);
    }
#endif
}
