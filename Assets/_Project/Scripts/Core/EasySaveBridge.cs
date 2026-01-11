using System;
using System.Linq;
using System.Reflection;

public static class EasySaveBridge
{
    static Type cachedEs3Type;
    static bool didSearch;

    public static bool IsAvailable
    {
        get
        {
            EnsureTypeCached();
            return cachedEs3Type != null;
        }
    }

    static void EnsureTypeCached()
    {
        if (didSearch) return;
        didSearch = true;

        try
        {
            // ES3 type is typically in global namespace.
            cachedEs3Type = AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(a =>
                {
                    try { return a.GetType("ES3", false); } catch { return null; }
                })
                .FirstOrDefault(t => t != null);
        }
        catch
        {
            cachedEs3Type = null;
        }
    }

    public static bool TryKeyExists(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        EnsureTypeCached();
        if (cachedEs3Type == null) return false;

        try
        {
            // bool ES3.KeyExists(string key)
            var mi = cachedEs3Type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "KeyExists" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            if (mi == null) return false;
            return (bool)mi.Invoke(null, new object[] { key });
        }
        catch
        {
            return false;
        }
    }

    public static bool TryLoadInt(string key, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(key)) return false;
        EnsureTypeCached();
        if (cachedEs3Type == null) return false;

        try
        {
            // Prefer: T ES3.Load<T>(string key, T defaultValue)
            var loadGeneric = cachedEs3Type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Load" && m.IsGenericMethodDefinition)
                .FirstOrDefault(m =>
                {
                    var p = m.GetParameters();
                    return p.Length == 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType.IsGenericParameter;
                });

            if (loadGeneric != null)
            {
                var mi = loadGeneric.MakeGenericMethod(typeof(int));
                object result = mi.Invoke(null, new object[] { key, 0 });
                if (result is int i)
                {
                    value = i;
                    return true;
                }
            }

            // Fallback: object ES3.Load(string key)
            var loadObj = cachedEs3Type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Load" && !m.IsGenericMethod && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            if (loadObj != null)
            {
                object result = loadObj.Invoke(null, new object[] { key });
                if (result is int i)
                {
                    value = i;
                    return true;
                }
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    public static bool TrySaveInt(string key, int value)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        EnsureTypeCached();
        if (cachedEs3Type == null) return false;

        try
        {
            // void ES3.Save(string key, object value)
            var save2 = cachedEs3Type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Save" && m.GetParameters().Length == 2
                                     && m.GetParameters()[0].ParameterType == typeof(string));
            if (save2 != null)
            {
                save2.Invoke(null, new object[] { key, value });
                return true;
            }

            // void ES3.Save<T>(string key, T value)
            var saveGeneric = cachedEs3Type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Save" && m.IsGenericMethodDefinition)
                .FirstOrDefault(m =>
                {
                    var p = m.GetParameters();
                    return p.Length == 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType.IsGenericParameter;
                });

            if (saveGeneric != null)
            {
                var mi = saveGeneric.MakeGenericMethod(typeof(int));
                mi.Invoke(null, new object[] { key, value });
                return true;
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }
}
