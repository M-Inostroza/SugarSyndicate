using UnityEngine;

// Internal component attached to each spawned visual instance
[DisallowMultipleComponent]
[AddComponentMenu("")] // hide from Add Component menu
public class BeltItemView : MonoBehaviour
{
    [HideInInspector]
    public int id;
}
