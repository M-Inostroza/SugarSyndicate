using UnityEngine;

public class ItemAgent : MonoBehaviour
{
    Vector2Int preferredDir;
    public void SpawnAt(Vector3 worldPos, Vector2Int dir)
    {
        transform.position = worldPos;
        preferredDir = dir; // store for your conveyor logic if needed
    }
}
