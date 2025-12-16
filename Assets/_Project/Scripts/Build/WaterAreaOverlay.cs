using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime overlay that drops a simple SpriteRenderer on every water cell.
/// Keeps it lightweight and renderer-agnostic (works with URP 2D/Default).
/// </summary>
public class WaterAreaOverlay : MonoBehaviour
{
    [Header("Setup")]
    [SerializeField] GridService grid;
    [SerializeField] bool matchGridLayer = true;

    [Header("Visuals")]
    [SerializeField] Color overlayColor = new Color(0.2f, 0.5f, 1f, 0.5f);
    [SerializeField, Min(0f)] float padding = 0.05f;
    [SerializeField] float zOffset = 0.05f; // base world Z for overlays
    [SerializeField] string sortingLayerName = "Default";
    [SerializeField] int sortingOrder = 1000;
    [SerializeField] bool visibleOnStart = false;

    static Sprite quadSprite; // 1x1 white full-rect sprite
    readonly List<SpriteRenderer> sprites = new();
    int sortingLayerId;

    public int LastQuadCount => sprites.Count;

    public static WaterAreaOverlay FindOrCreate(GridService gridService)
    {
        if (gridService == null) return null;
        var overlay = gridService.GetComponent<WaterAreaOverlay>();
        if (overlay == null) overlay = gridService.gameObject.AddComponent<WaterAreaOverlay>();
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
        var color = overlayColor.a > 0f ? overlayColor : grid.WaterColor;
        float z = ResolveZ();

        for (int y = 0; y < size.y; y++)
        {
            for (int x = 0; x < size.x; x++)
            {
                var c = new Vector2Int(x, y);
                if (!grid.IsWater(c)) continue;
                var world = grid.CellToWorld(c, z);
                AddSprite(world, final, color);
            }
        }
    }

    void AddSprite(Vector3 worldPos, float size, Color color)
    {
        EnsureSprite();
        var go = new GameObject("WaterCellOverlay");
        go.transform.SetParent(transform, false);
        go.layer = gameObject.layer;
        go.transform.position = worldPos;
        go.transform.localScale = Vector3.one;

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = quadSprite;
        sr.color = color;
        sr.sortingLayerID = sortingLayerId;
        sr.sortingOrder = sortingOrder;
        sr.drawMode = SpriteDrawMode.Simple;
        sr.size = new Vector2(size, size);
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
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "WaterOverlayTex" };
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        quadSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        quadSprite.name = "WaterOverlaySprite";
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
}
