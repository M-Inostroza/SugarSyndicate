using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

public interface IMachineStorage
{
    int StoredItemCount { get; }
}

// Optional extension for machines that want to display N / N.
public interface IMachineStorageWithCapacity : IMachineStorage
{
    int Capacity { get; }
}

[DisallowMultipleComponent]
public class MachineStorageDisplay : MonoBehaviour
{
    [SerializeField] Vector3 worldOffset = new Vector3(0f, 0.6f, 0f);
    [SerializeField, Min(0.1f)] float fontSize = 2.5f;
    [SerializeField] Color textColor = Color.white;
    [SerializeField] bool hideWhenZero = false;
    [SerializeField] int sortingOrderOffset = 10;
    [SerializeField] string sortingLayerName = "Default";

    IMachineStorage storage;
    TextMeshPro text;
    int lastCount = int.MinValue;

    void Awake()
    {
        storage = GetComponent<IMachineStorage>() ?? GetComponentInParent<IMachineStorage>();
        EnsureText();
        UpdateText(true);
        UpdateTransform();
    }

    void LateUpdate()
    {
        if (text == null || storage == null) return;
        UpdateText(false);
        UpdateTransform();
    }

    void EnsureText()
    {
        text = GetComponentInChildren<TextMeshPro>(true);
        if (text == null)
        {
            var go = new GameObject("StorageCount");
            go.transform.SetParent(transform, false);
            text = go.AddComponent<TextMeshPro>();
        }

        text.text = "0";
        text.fontSize = fontSize;
        text.color = textColor;
        text.alignment = TextAlignmentOptions.Center;
        text.richText = false;
        text.enableAutoSizing = false;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.transform.rotation = Quaternion.identity;

        ApplySorting(text);
    }

    void UpdateTransform()
    {
        text.transform.position = transform.position + worldOffset;
        text.transform.rotation = Quaternion.identity;
    }

    void UpdateText(bool force)
    {
        if (storage == null) return;
        int count = storage.StoredItemCount;
        if (!force && count == lastCount) return;
        lastCount = count;

        if (hideWhenZero && count <= 0)
            text.text = string.Empty;
        else
        {
            if (storage is IMachineStorageWithCapacity capped)
                text.text = $"{count} / {Mathf.Max(0, capped.Capacity)}";
            else
                text.text = count.ToString();
        }
    }

    void ApplySorting(TextMeshPro target)
    {
        if (target == null) return;
        var renderer = target.GetComponent<Renderer>();
        if (renderer == null) return;

        int layerId = SortingLayer.NameToID(sortingLayerName);
        int order = 0;

        var group = GetComponentInChildren<SortingGroup>();
        if (group != null)
        {
            layerId = group.sortingLayerID;
            order = group.sortingOrder;
        }
        else
        {
            var sr = GetComponentInChildren<SpriteRenderer>();
            if (sr != null)
            {
                layerId = sr.sortingLayerID;
                order = sr.sortingOrder;
            }
        }

        renderer.sortingLayerID = layerId;
        renderer.sortingOrder = order + sortingOrderOffset;
    }
}
