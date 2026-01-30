using UnityEngine;

/// <summary>
/// Lightweight runtime overlay that highlights a single grid cell.
/// </summary>
public class TutorialCellOverlay : MonoBehaviour
{
    [Header("Setup")]
    [SerializeField] GridService grid;
    [SerializeField] bool matchGridLayer = true;

    [Header("Visuals")]
    [SerializeField] Color highlightColor = new Color(1f, 0.9f, 0.2f, 0.85f);
    [SerializeField, Min(0f)] float padding = 0.05f;
    [SerializeField] float zOffset = 0.1f;
    [SerializeField] string sortingLayerName = "Default";
    [SerializeField] int sortingOrder = 1200;

    [Header("Pulse")]
    [SerializeField] bool pulse = true;
    [SerializeField, Min(0.1f)] float pulseSpeed = 2f;
    [SerializeField, Range(1f, 1.5f)] float pulseScale = 1.08f;
    [SerializeField] bool useUnscaledTime = true;

    static Sprite quadSprite;
    readonly System.Collections.Generic.List<SpriteRenderer> sprites = new System.Collections.Generic.List<SpriteRenderer>();
    int sortingLayerId;
    readonly System.Collections.Generic.List<Vector2Int> currentCells = new System.Collections.Generic.List<Vector2Int>();
    float baseSize = 1f;
    bool isVisible;

    public static TutorialCellOverlay FindOrCreate(GridService gridService)
    {
        if (gridService == null) return null;
        var overlay = gridService.GetComponent<TutorialCellOverlay>();
        if (overlay == null) overlay = gridService.gameObject.AddComponent<TutorialCellOverlay>();
        if (overlay.grid == null) overlay.grid = gridService;
        overlay.Rebuild();
        overlay.Hide();
        return overlay;
    }

    void Awake()
    {
        sortingLayerId = SortingLayer.NameToID(sortingLayerName);
        if (sortingLayerId == 0) sortingLayerId = SortingLayer.NameToID("Default");
        if (grid == null) grid = GridService.Instance != null ? GridService.Instance : GetComponent<GridService>();
        if (matchGridLayer && grid != null) gameObject.layer = grid.gameObject.layer;
        EnsureSpritePool(1);
        ApplyVisualDefaults();
    }

    void Update()
    {
        if (!isVisible || sprites.Count == 0 || !pulse) return;
        float t = Mathf.Sin((useUnscaledTime ? Time.unscaledTime : Time.time) * pulseSpeed);
        float scale = Mathf.Lerp(1f, pulseScale, (t + 1f) * 0.5f);
        for (int i = 0; i < sprites.Count; i++)
        {
            var sr = sprites[i];
            if (sr == null || !sr.enabled) continue;
            sr.transform.localScale = Vector3.one * scale;
        }
    }

    public void ShowCell(Vector2Int cell)
    {
        ShowCells(new[] { cell });
    }

    public void ShowCells(System.Collections.Generic.IReadOnlyList<Vector2Int> cells)
    {
        if (grid == null) grid = GridService.Instance ?? FindAnyObjectByType<GridService>();
        if (grid == null || cells == null || cells.Count == 0) return;

        currentCells.Clear();
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            if (grid.InBounds(c) && !currentCells.Contains(c))
                currentCells.Add(c);
        }
        if (currentCells.Count == 0) return;
        Rebuild();
        SetVisible(true);
    }

    public void Hide()
    {
        SetVisible(false);
    }

    void SetVisible(bool visible)
    {
        isVisible = visible;
        for (int i = 0; i < sprites.Count; i++)
        {
            if (sprites[i] != null)
                sprites[i].enabled = visible && i < currentCells.Count;
        }
    }

    public void Rebuild()
    {
        if (grid == null) grid = GridService.Instance ?? FindAnyObjectByType<GridService>();
        if (grid == null) return;
        if (matchGridLayer) gameObject.layer = grid.gameObject.layer;
        EnsureSpritePool(Mathf.Max(1, currentCells.Count));
        ApplyVisualDefaults();
        UpdateTransform();
    }

    void UpdateTransform()
    {
        if (sprites.Count == 0 || grid == null) return;
        float cellSize = grid.CellSize;
        float maxPad = Mathf.Max(0f, cellSize * 0.5f - 0.001f);
        float pad = Mathf.Clamp(padding, 0f, maxPad);
        baseSize = Mathf.Max(0.001f, cellSize - pad * 2f);
        float z = ResolveZ();
        for (int i = 0; i < sprites.Count; i++)
        {
            var sr = sprites[i];
            if (sr == null) continue;
            sr.drawMode = SpriteDrawMode.Simple;
            sr.size = new Vector2(baseSize, baseSize);
            if (i < currentCells.Count)
                sr.transform.position = grid.CellToWorld(currentCells[i], z);
            sr.transform.localScale = Vector3.one;
        }
    }

    void EnsureSpritePool(int count)
    {
        if (count < 1) count = 1;
        EnsureSprite();
        while (sprites.Count < count)
        {
            var go = new GameObject("TutorialCellHighlight");
            go.transform.SetParent(transform, false);
            go.layer = gameObject.layer;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = quadSprite;
            sr.color = highlightColor;
            sr.sortingLayerID = sortingLayerId;
            sr.sortingOrder = sortingOrder;
            sr.drawMode = SpriteDrawMode.Simple;
            sr.enabled = false;
            sprites.Add(sr);
        }
    }

    void ApplyVisualDefaults()
    {
        for (int i = 0; i < sprites.Count; i++)
        {
            var sr = sprites[i];
            if (sr == null) continue;
            sr.sprite = quadSprite;
            sr.color = highlightColor;
            sr.sortingLayerID = sortingLayerId;
            sr.sortingOrder = sortingOrder;
        }
    }

    void EnsureSprite()
    {
        if (quadSprite != null) return;
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "TutorialCellOverlayTex" };
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        quadSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        quadSprite.name = "TutorialCellOverlaySprite";
    }

    float ResolveZ()
    {
        float z = zOffset;
        var cam = Camera.main;
        if (cam != null)
            z = Mathf.Max(z, cam.nearClipPlane + 0.05f);
        return z;
    }
}
