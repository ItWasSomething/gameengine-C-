using System.Collections;
using System.Numerics;
using System.Reflection;

namespace Engine.Core;

// One saved field of a component (or of a [Serializable] class inside one),
// plus the attributes that shape how the Inspector shows it.
public sealed class SerializedField
{
    public FieldInfo Field { get; }
    public RangeAttribute? Range { get; }
    public IReadOnlyList<string> Headers { get; }
    public string? Tooltip { get; }
    public bool HideInInspector { get; }

    public SerializedField(FieldInfo field)
    {
        Field = field;
        Range = field.GetCustomAttribute<RangeAttribute>();
        Headers = field.GetCustomAttributes<HeaderAttribute>().Select(h => h.header).ToList();
        Tooltip = field.GetCustomAttribute<TooltipAttribute>()?.tooltip;
        HideInInspector = field.IsDefined(typeof(HideInInspectorAttribute));
    }

    public string Name => Field.Name;
    public Type Type => Field.FieldType;
    public object? GetValue(object target) => Field.GetValue(target);
    public void SetValue(object target, object? value) => Field.SetValue(target, value);
}

// Knows every component type by name (scene files store "MeshRenderer", and
// loading looks the class back up here) and which fields of each type get
// saved. Game script assemblies are added with RegisterAssembly.
public static class TypeRegistry
{
    private static readonly Dictionary<string, Type> Components = new();
    private static readonly Dictionary<Type, IReadOnlyList<SerializedField>> FieldCache = new();

    static TypeRegistry() => RegisterAssembly(typeof(Component).Assembly);

    // Raised when the set of types changes (e.g. scripts recompiled), so UI
    // lists like Add Component can refresh.
    public static event Action? Changed;

    public static void RegisterAssembly(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsGenericType || !typeof(Component).IsAssignableFrom(type)) continue;
            if (type == typeof(MissingComponent) || type.GetConstructor(Type.EmptyTypes) == null) continue;
            Components[type.Name] = type;   // later registrations win: a reloaded script replaces its old version
        }
        FieldCache.Clear();
        Changed?.Invoke();
    }

    public static void Unregister(Func<Type, bool> predicate)
    {
        foreach (var key in Components.Where(kv => predicate(kv.Value)).Select(kv => kv.Key).ToList())
            Components.Remove(key);
        FieldCache.Clear();
        Changed?.Invoke();
    }

    public static Type? FindComponentType(string name) => Components.GetValueOrDefault(name);

    // Everything that can be added through Add Component (Transform can't).
    public static IEnumerable<Type> AddableComponents =>
        Components.Values.Where(t => t != typeof(Transform)).OrderBy(MenuPath);

    // "Rendering/Light", or "Scripts/MyScript" for scripts without an
    // [AddComponentMenu].
    public static string MenuPath(Type type) =>
        type.GetCustomAttribute<AddComponentMenuAttribute>()?.componentMenu ?? "Scripts/" + type.Name;

    // Unity's rule, base classes first:
    //   public, non-static, not [NonSerialized], not readonly   → saved
    //   private/protected with [SerializeField]                  → saved
    // and only if the field's type is one we know how to save.
    public static IReadOnlyList<SerializedField> GetSerializedFields(Type type)
    {
        if (FieldCache.TryGetValue(type, out var cached)) return cached;

        var chain = new List<Type>();
        for (var t = type; t != null && t != typeof(object) && t != typeof(Component) && t != typeof(Behaviour)
                           && t != typeof(MonoBehaviour); t = t.BaseType)
            chain.Insert(0, t);

        var list = new List<SerializedField>();
        foreach (var t in chain)
        {
            var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .OrderBy(f => f.MetadataToken);   // = source order
            foreach (var f in fields)
            {
                if (f.IsInitOnly || f.IsNotSerialized || f.Name.Contains('<')) continue;   // '<' = compiler-generated backing field
                bool include = f.IsPublic || f.IsDefined(typeof(SerializeFieldAttribute));
                if (include && IsSerializableType(f.FieldType)) list.Add(new SerializedField(f));
            }
        }
        FieldCache[type] = list;
        return list;
    }

    public static bool IsSerializableType(Type type)
    {
        if (type.IsPrimitive || type == typeof(string) || type.IsEnum) return true;
        if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Quaternion)) return true;
        if (typeof(EngineObject).IsAssignableFrom(type)) return true;              // stored as a reference
        if (type.IsArray) return type.GetArrayRank() == 1 && IsSerializableType(type.GetElementType()!);
        if (IsList(type, out var element)) return IsSerializableType(element);
        // Your own [Serializable] classes/structs are saved field by field.
        return type.IsDefined(typeof(SerializableAttribute)) && !type.IsAbstract &&
               (type.IsValueType || type.GetConstructor(Type.EmptyTypes) != null);
    }

    public static bool IsList(Type type, out Type element)
    {
        element = typeof(object);
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(List<>)) return false;
        element = type.GetGenericArguments()[0];
        return true;
    }

    // Creates an empty array or list of `type` with `count` default elements.
    public static IList CreateCollection(Type type, int count)
    {
        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, count);
        var list = (IList)Activator.CreateInstance(type)!;
        IsList(type, out var element);
        for (int i = 0; i < count; i++) list.Add(DefaultValue(element));
        return list;
    }

    public static Type ElementType(Type collectionType) =>
        collectionType.IsArray ? collectionType.GetElementType()! : collectionType.GetGenericArguments()[0];

    public static object? DefaultValue(Type type)
    {
        if (type == typeof(string)) return "";
        if (type.IsValueType) return Activator.CreateInstance(type);
        if (typeof(EngineObject).IsAssignableFrom(type)) return null;
        if (type.IsArray || IsList(type, out _)) return CreateCollection(type, 0);
        return type.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(type) : null;
    }
}

// Stands in for a component whose script type can't be found (deleted,
// renamed, or failed to compile). It keeps the original saved data so
// nothing is lost — fix the script and the component comes back intact.
public sealed class MissingComponent : Component
{
    public string scriptName = "";
    internal System.Text.Json.Nodes.JsonObject? originalJson;
}
