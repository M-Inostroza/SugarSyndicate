using System.Collections.Generic;
using UnityEngine;

public class PowerCable : MonoBehaviour
{
    [SerializeField] GridService grid;
    [SerializeField] PowerService powerService;
    [SerializeField] Direction direction = Direction.Right;
    [SerializeField] SpriteRenderer spriteRenderer;
    [SerializeField] Sprite straightSprite;
    [SerializeField] Sprite curveSprite;
    [SerializeField] Sprite teeSprite;
    [SerializeField] Sprite crossSprite;
    [SerializeField] CurveCorner curveBaseCorner = CurveCorner.LeftUp;
    [SerializeField] Direction teeBaseMissingDirection = Direction.Down;

    [System.NonSerialized] public bool isGhost = false;

    Vector2Int cell;
    bool registered;
    bool registeredAsBlueprint;
    public Vector2Int Cell => cell;

    static readonly Dictionary<Vector2Int, PowerCable> cablesByCell = new();

    public static bool TryGetAtCell(Vector2Int cell, out PowerCable cable)
    {
        if (cablesByCell.TryGetValue(cell, out cable))
            return cable != null;
        cable = null;
        return false;
    }
    public enum CurveCorner
    {
        UpRight,
        RightDown,
        DownLeft,
        LeftUp,
    }

    [System.Flags]
    public enum ConnectionMask
    {
        None = 0,
        Up = 1 << 0,
        Right = 1 << 1,
        Down = 1 << 2,
        Left = 1 << 3,
    }

    [SerializeField] ConnectionMask baseConnections = ConnectionMask.None;
    bool hasExplicitConnections;

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
        UndergroundVisibilityRegistry.RegisterPowerCable(this);
        Register();
        if (registered)
        {
            RegisterInMap();
            RefreshSelfAndNeighbors();
        }
    }

    void Start()
    {
        if (!hasExplicitConnections)
            InitializeBaseConnectionsFromCables();
        RefreshVisual();
    }

    void OnDisable()
    {
        bool removed = UnregisterFromMap();
        if (removed)
        {
            RemoveNeighborBaseConnections();
            RefreshNeighbors(cell);
        }
        UndergroundVisibilityRegistry.UnregisterPowerCable(this);
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
        if (registered)
        {
            RegisterInMap();
            RefreshSelfAndNeighbors();
        }
    }

    public void SetDirection(Direction newDirection)
    {
        SetStraight(newDirection);
    }

    public void SetStraight(Direction newDirection)
    {
        direction = newDirection;
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

    public static ConnectionMask MaskFromDirection(Direction dir)
    {
        return dir switch
        {
            Direction.Up => ConnectionMask.Up,
            Direction.Right => ConnectionMask.Right,
            Direction.Down => ConnectionMask.Down,
            Direction.Left => ConnectionMask.Left,
            _ => ConnectionMask.None,
        };
    }

    public void SetBaseConnections(ConnectionMask mask, bool refresh = true)
    {
        baseConnections = mask;
        hasExplicitConnections = true;
        if (refresh)
            RefreshVisual();
    }

    public void ClearBaseConnection(Direction dir, bool refresh = true)
    {
        if (!hasExplicitConnections)
            hasExplicitConnections = true;
        baseConnections &= ~MaskFromDirection(dir);
        if (refresh)
            RefreshVisual();
    }

    bool HasBaseConnection(Direction dir) => (baseConnections & MaskFromDirection(dir)) != 0;

    public static void RefreshAround(Vector2Int center)
    {
        RefreshAt(center);
        RefreshAt(center + Vector2Int.up);
        RefreshAt(center + Vector2Int.right);
        RefreshAt(center + Vector2Int.down);
        RefreshAt(center + Vector2Int.left);
    }

    static void RefreshAt(Vector2Int cell)
    {
        if (!cablesByCell.TryGetValue(cell, out var cable) || cable == null) return;
        cable.RefreshVisual();
    }

    void RefreshSelfAndNeighbors()
    {
        RefreshAround(cell);
    }

    void RefreshNeighbors(Vector2Int center)
    {
        RefreshAround(center);
    }

    void RegisterInMap()
    {
        cablesByCell[cell] = this;
    }

    bool UnregisterFromMap()
    {
        if (cablesByCell.TryGetValue(cell, out var existing) && existing == this)
        {
            cablesByCell.Remove(cell);
            return true;
        }
        return false;
    }

    void RefreshVisual()
    {
        if (spriteRenderer == null) return;

        var power = powerService ?? PowerService.Instance;
        if (power == null) return;

        bool up = HasBaseConnection(Direction.Up) || HasDynamicConnection(power, cell + Vector2Int.up);
        bool right = HasBaseConnection(Direction.Right) || HasDynamicConnection(power, cell + Vector2Int.right);
        bool down = HasBaseConnection(Direction.Down) || HasDynamicConnection(power, cell + Vector2Int.down);
        bool left = HasBaseConnection(Direction.Left) || HasDynamicConnection(power, cell + Vector2Int.left);

        int count = (up ? 1 : 0) + (right ? 1 : 0) + (down ? 1 : 0) + (left ? 1 : 0);
        if (count <= 0)
            return;

        if (count == 1)
        {
            SetStraight(FirstDirection(up, right, down, left));
            return;
        }

        if (count == 2)
        {
            if ((up && down) || (left && right))
            {
                SetStraight(up ? Direction.Up : Direction.Right);
                return;
            }

            var first = FirstDirection(up, right, down, left);
            var second = SecondDirection(up, right, down, left, first);
            SetCurve(first, second);
            return;
        }

        if (count == 3)
        {
            var missing = !up ? Direction.Up : !right ? Direction.Right : !down ? Direction.Down : Direction.Left;
            SetTJunction(missing);
            return;
        }

        SetCross();
    }

    bool HasDynamicConnection(PowerService power, Vector2Int targetCell)
    {
        if (power == null) return false;
        if (power.IsPoleOrBlueprintAt(targetCell)) return true;
        return power.AllowsTerminalConnection(targetCell, cell);
    }

    static Direction FirstDirection(bool up, bool right, bool down, bool left)
    {
        if (up) return Direction.Up;
        if (right) return Direction.Right;
        if (down) return Direction.Down;
        return Direction.Left;
    }

    static Direction SecondDirection(bool up, bool right, bool down, bool left, Direction first)
    {
        if (first != Direction.Up && up) return Direction.Up;
        if (first != Direction.Right && right) return Direction.Right;
        if (first != Direction.Down && down) return Direction.Down;
        return Direction.Left;
    }

    void SetTJunction(Direction missing)
    {
        if (spriteRenderer == null || teeSprite == null)
        {
            SetStraight(DirectionUtil.Opposite(missing));
            return;
        }
        spriteRenderer.sprite = teeSprite;
        direction = DirectionUtil.Opposite(missing);
        var baseMissing = DirectionUtil.IsCardinal(teeBaseMissingDirection) ? teeBaseMissingDirection : Direction.Down;
        float baseAngle = DirectionToAngle(baseMissing);
        float targetAngle = DirectionToAngle(missing);
        float angle = Mathf.Repeat(targetAngle - baseAngle, 360f);
        transform.rotation = Quaternion.Euler(0f, 0f, angle);
    }

    void SetCross()
    {
        if (spriteRenderer == null || crossSprite == null)
        {
            SetStraight(Direction.Right);
            return;
        }
        spriteRenderer.sprite = crossSprite;
        direction = Direction.Right;
        transform.rotation = Quaternion.identity;
    }

    void InitializeBaseConnectionsFromCables()
    {
        var power = powerService ?? PowerService.Instance;
        if (power == null) return;
        ConnectionMask mask = ConnectionMask.None;
        if (power.IsCableAt(cell + Vector2Int.up)) mask |= ConnectionMask.Up;
        if (power.IsCableAt(cell + Vector2Int.right)) mask |= ConnectionMask.Right;
        if (power.IsCableAt(cell + Vector2Int.down)) mask |= ConnectionMask.Down;
        if (power.IsCableAt(cell + Vector2Int.left)) mask |= ConnectionMask.Left;
        baseConnections = mask;
        hasExplicitConnections = true;
    }

    void RemoveNeighborBaseConnections()
    {
        ClearNeighborConnection(cell + Vector2Int.up, Direction.Down);
        ClearNeighborConnection(cell + Vector2Int.right, Direction.Left);
        ClearNeighborConnection(cell + Vector2Int.down, Direction.Up);
        ClearNeighborConnection(cell + Vector2Int.left, Direction.Right);
    }

    void ClearNeighborConnection(Vector2Int neighborCell, Direction directionToRemoved)
    {
        if (!cablesByCell.TryGetValue(neighborCell, out var neighbor) || neighbor == null) return;
        neighbor.ClearBaseConnection(directionToRemoved, refresh: false);
    }

    static float DirectionToAngle(Direction dir)
    {
        return dir switch
        {
            Direction.Right => 0f,
            Direction.Up => 90f,
            Direction.Left => 180f,
            Direction.Down => 270f,
            _ => 0f,
        };
    }
}
