using System.Collections.Generic;
using UnityEngine;

public class Pool<T> where T : Component
{
    readonly T prefab;
    readonly Transform parent;
    readonly Stack<T> stack = new();

    public Pool(T prefab, int prewarm, Transform parent = null)
    {
        this.prefab = prefab;
        this.parent = parent;
        for (int i = 0; i < prewarm; i++)
        {
            var obj = Object.Instantiate(prefab, parent);
            obj.gameObject.SetActive(false);
            stack.Push(obj);
        }
    }

    public T Get()
    {
        var obj = stack.Count > 0 ? stack.Pop() : Object.Instantiate(prefab, parent);
        obj.gameObject.SetActive(true);
        return obj;
    }

    public void Release(T obj)
    {
        obj.gameObject.SetActive(false);
        stack.Push(obj);
    }
}
