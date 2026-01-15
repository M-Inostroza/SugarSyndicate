using UnityEngine;

public class PowerCable : MonoBehaviour
{
    [SerializeField] GridService grid;
    [SerializeField] PowerService powerService;
    [SerializeField] Direction direction = Direction.Right;
    [SerializeField] SpriteRenderer spriteRenderer;
    [SerializeField] Sprite straightSprite;
    [SerializeField] Sprite curveSprite;
    [SerializeField] CurveCorner curveBaseCorner = CurveCorner.LeftUp;

    [System.NonSerialized] public bool isGhost = false;

    Vector2Int cell;
    bool registered;
    bool registeredAsBlueprint;
    bool isCurve;

    public enum CurveCorner
    {
        UpRight,
        RightDown,
        DownLeft,
        LeftUp,
    }

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
        if (powerService == null) powerService = PowerService.EnsureInstance();
        if (spriteRenderer == null)
            spriteRenderer = GetComponentInChildren<SpriteRenderer>(true);
        if (straightSprite == null && spriteRenderer != null)
            straightSprite = spriteRenderer.sprite;
    }

    void OnEnable()
    {
        Register();
    }

    void OnDisable()
    {
        Unregister();
    }

    void Register()
    {
        if (registered) return;
        if (grid == null) grid = GridService.Instance;
        if (grid == null) return;
        cell = grid.WorldToCell(transform.position);
        if (powerService == null) powerService = PowerService.EnsureInstance();
        if (powerService != null)
        {
            bool ok = isGhost ? powerService.RegisterCableBlueprint(cell) : powerService.RegisterCable(cell);
            if (!ok)
            {
                Debug.LogWarning($"[PowerCable] Cell {cell} already occupied by a pole/cable.");
                return;
            }
        }
        registered = true;
        registeredAsBlueprint = isGhost;
    }

    void Unregister()
    {
        if (!registered) return;
        if (powerService == null) powerService = PowerService.Instance;
        if (registeredAsBlueprint) powerService?.UnregisterCableBlueprint(cell);
        else powerService?.UnregisterCable(cell);
        registered = false;
        registeredAsBlueprint = false;
    }

    public void SetGhost(bool ghost)
    {
        if (isGhost == ghost) return;
        isGhost = ghost;
        if (!registered) return;
        Unregister();
        Register();
    }

    public void ActivateFromBlueprint()
    {
        isGhost = false;
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        if (grid == null) grid = GridService.Instance;
        if (grid != null)
            cell = grid.WorldToCell(transform.position);
        if (powerService != null)
        {
            if (powerService.PromoteCableBlueprint(cell))
            {
                registered = true;
                registeredAsBlueprint = false;
            }
        }
    }

    public void SetDirection(Direction newDirection)
    {
        SetStraight(newDirection);
    }

    public void SetStraight(Direction newDirection)
    {
        direction = newDirection;
        isCurve = false;
        if (spriteRenderer != null && straightSprite != null)
            spriteRenderer.sprite = straightSprite;
        ApplyRotation();
    }

    public void SetCurve(Direction fromDirection, Direction toDirection)
    {
        direction = toDirection;
        if (curveSprite == null || spriteRenderer == null)
        {
            SetStraight(toDirection);
            return;
        }

        isCurve = true;
        spriteRenderer.sprite = curveSprite;
        ApplyCurveRotation(fromDirection, toDirection);
    }

    void ApplyRotation()
    {
        float angle = direction switch
        {
            Direction.Right => 0f,
            Direction.Up => 90f,
            Direction.Left => 180f,
            Direction.Down => 270f,
            _ => 0f,
        };
        transform.rotation = Quaternion.Euler(0f, 0f, angle);
    }

    void ApplyCurveRotation(Direction fromDirection, Direction toDirection)
    {
        float angle;
        if (IsCurvePair(fromDirection, toDirection, Direction.Up, Direction.Right)) angle = 0f;
        else if (IsCurvePair(fromDirection, toDirection, Direction.Right, Direction.Down)) angle = 270f;
        else if (IsCurvePair(fromDirection, toDirection, Direction.Down, Direction.Left)) angle = 180f;
        else if (IsCurvePair(fromDirection, toDirection, Direction.Left, Direction.Up)) angle = 90f;
        else
        {
            SetStraight(toDirection);
            return;
        }
        angle = Mathf.Repeat(angle + GetCurveBaseOffset(), 360f);
        transform.rotation = Quaternion.Euler(0f, 0f, angle);
    }

    static bool IsCurvePair(Direction a, Direction b, Direction d1, Direction d2)
        => (a == d1 && b == d2) || (a == d2 && b == d1);

    float GetCurveBaseOffset()
    {
        float baseAngle = curveBaseCorner switch
        {
            CurveCorner.UpRight => 0f,
            CurveCorner.RightDown => 270f,
            CurveCorner.DownLeft => 180f,
            CurveCorner.LeftUp => 90f,
            _ => 0f,
        };
        return Mathf.Repeat(360f - baseAngle, 360f);
    }
}
