using System.Collections;
using System.Reflection;

namespace Engine.Core;

// Something attached to a GameObject that gives it data or behavior. A
// Component always belongs to exactly one GameObject, and reaches the rest
// of its object through the shortcuts below (transform, GetComponent...).
public abstract class Component : EngineObject
{
    public GameObject gameObject { get; internal set; } = null!;
    public Transform transform => gameObject.transform;

    // A component's name *is* its GameObject's name.
    public override string name
    {
        get => gameObject?.name ?? GetType().Name;
        set { if (gameObject != null) gameObject.name = value; }
    }

    public string tag
    {
        get => gameObject.tag;
        set => gameObject.tag = value;
    }

    public bool CompareTag(string other) => gameObject.tag == other;

    public T? GetComponent<T>() where T : class => gameObject.GetComponent<T>();
    public Component? GetComponent(Type type) => gameObject.GetComponent(type);
    public bool TryGetComponent<T>(out T component) where T : class => gameObject.TryGetComponent(out component);
    public T[] GetComponents<T>() where T : class => gameObject.GetComponents<T>();
    public T? GetComponentInChildren<T>(bool includeInactive = false) where T : class => gameObject.GetComponentInChildren<T>(includeInactive);
    public T[] GetComponentsInChildren<T>(bool includeInactive = false) where T : class => gameObject.GetComponentsInChildren<T>(includeInactive);
    public T? GetComponentInParent<T>() where T : class => gameObject.GetComponentInParent<T>();

    // ---- Lifecycle plumbing (called by Scene) -------------------------------------
    // Only MonoBehaviour does anything with these; built-in components like
    // Light or MeshRenderer are pure data that engine systems read.

    internal bool awoken;
    internal virtual void CallAwake() { awoken = true; }
    internal virtual void CallOnEnable() { }
    internal virtual void CallOnDisable() { }
    internal virtual void CallOnDestroy() { }

    // Editor hook: called after the Inspector changes a field (Unity's
    // OnValidate), so a component can clamp or react to values.
    internal virtual void CallOnValidate() { }
}

// A component that can be switched on and off with the checkbox in its
// Inspector header. Disabled behaviours get no Update calls.
public abstract class Behaviour : Component
{
    private bool _enabled = true;

    public bool enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            gameObject?.scene?.OnBehaviourEnabledChanged(this, value);
        }
    }

    // Sets the flag without notifying anyone — for the loader.
    internal void SetEnabledSilently(bool value) => _enabled = value;

    public bool isActiveAndEnabled => _enabled && gameObject != null && gameObject.activeInHierarchy;
}

// Base class for your own scripts. Write any of these methods (they can be
// private, exactly like Unity — the engine finds them by name):
//
//   Awake()        once, when the object first becomes active
//   OnEnable()     every time it becomes enabled/active
//   Start()        once, just before its first Update
//   FixedUpdate()  every physics step (fixed rate)
//   Update()       every frame
//   LateUpdate()   every frame, after all Updates (cameras that follow things)
//   OnDisable()    every time it becomes disabled/inactive
//   OnDestroy()    once, when destroyed
//   OnValidate()   in the editor, after a field is changed in the Inspector
public abstract class MonoBehaviour : Behaviour
{
    // Looking methods up by name with reflection is slow, so it's done once
    // per script *type* and cached; each instance then binds its own fast
    // delegates the first time it's used.
    private static readonly Dictionary<Type, MethodInfo?[]> MessageCache = new();

    private enum Message { Awake, OnEnable, Start, FixedUpdate, Update, LateUpdate, OnDisable, OnDestroy, OnValidate }
    private static readonly string[] MessageNames = Enum.GetNames<Message>();

    private Action?[]? _messages;
    private readonly List<CoroutineState> _coroutines = new();

    internal bool started;

    private Action? GetMessage(Message message)
    {
        if (_messages == null)
        {
            var methods = GetMethodsFor(GetType());
            _messages = methods.Select(m => m == null ? null : (Action)m.CreateDelegate(typeof(Action), this)).ToArray();
        }
        return _messages[(int)message];
    }

    // Hot-reload: forget cached methods of script types from an unloaded
    // assembly (holding them would also keep the old code alive in memory).
    internal static void ClearMessageCache(Assembly assembly)
    {
        foreach (var type in MessageCache.Keys.Where(t => t.Assembly == assembly).ToList())
            MessageCache.Remove(type);
    }

    // " (Player.cs:42)" — the first stack frame that has a file and line,
    // which for script errors is the line in your script.
    internal static string SourceLocation(Exception e)
    {
        var frame = new System.Diagnostics.StackTrace(e, fNeedFileInfo: true).GetFrames()
            .FirstOrDefault(f => f.GetFileName() != null && f.GetFileLineNumber() > 0);
        return frame == null ? "" : $" ({Path.GetFileName(frame.GetFileName())}:{frame.GetFileLineNumber()})";
    }

    private static MethodInfo?[] GetMethodsFor(Type type)
    {
        if (MessageCache.TryGetValue(type, out var cached)) return cached;
        var result = new MethodInfo?[MessageNames.Length];
        for (int i = 0; i < MessageNames.Length; i++)
        {
            // Walk up the class chain: a private Update() in a base script
            // isn't visible from the derived type through GetMethod alone.
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                var m = t.GetMethod(MessageNames[i],
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    Type.EmptyTypes);
                if (m != null && m.ReturnType == typeof(void)) { result[i] = m; break; }
            }
        }
        MessageCache[type] = result;
        return result;
    }


    private void Send(Message message)
    {
        var action = GetMessage(message);
        if (action == null) return;
        try { action(); }
        catch (Exception e)
        {
            var inner = e is TargetInvocationException { InnerException: { } ie } ? ie : e;
            Debug.LogError($"{GetType().Name}.{message} on '{name}': {inner.GetType().Name}: {inner.Message}{SourceLocation(inner)}");
        }
    }

    internal override void CallAwake() { awoken = true; Send(Message.Awake); }
    internal override void CallOnEnable() => Send(Message.OnEnable);
    internal void CallStart() { started = true; Send(Message.Start); }
    internal void CallFixedUpdate() => Send(Message.FixedUpdate);
    internal void CallUpdate() => Send(Message.Update);
    internal void CallLateUpdate() => Send(Message.LateUpdate);
    internal override void CallOnDisable() => Send(Message.OnDisable);
    internal override void CallOnDestroy() { StopAllCoroutines(); Send(Message.OnDestroy); }
    internal override void CallOnValidate() => Send(Message.OnValidate);

    // ---- Coroutines ----------------------------------------------------------------
    //
    // A coroutine is a method that can pause itself with `yield return` and
    // pick up where it left off later. C# compiles an IEnumerator method into
    // a little state machine; each MoveNext() runs it to the next yield. The
    // engine simply calls MoveNext() again when the yielded wait is over:
    //
    //   yield return null;                      → next frame
    //   yield return new WaitForSeconds(0.5f);  → after half a second
    //   yield return new WaitUntil(() => done); → once the condition is true
    //   yield return StartCoroutine(Other());   → after another coroutine ends

    private sealed class CoroutineState
    {
        public required IEnumerator Routine;
        public required Coroutine Handle;
        public object? Waiting;
        public float WaitUntilTime;
    }

    public Coroutine StartCoroutine(IEnumerator routine)
    {
        var handle = new Coroutine();
        var state = new CoroutineState { Routine = routine, Handle = handle };
        _coroutines.Add(state);
        // Runs immediately up to its first yield, like Unity.
        if (!Advance(state)) Finish(state);
        return handle;
    }

    public void StopCoroutine(Coroutine? handle)
    {
        var state = _coroutines.FirstOrDefault(s => s.Handle == handle);
        if (state != null) Finish(state);
    }

    public void StopAllCoroutines()
    {
        foreach (var state in _coroutines.ToList()) Finish(state);
    }

    // Calls a method by name after a delay — Unity's Invoke().
    public void Invoke(string methodName, float time) => StartCoroutine(InvokeRoutine(methodName, time));

    private IEnumerator InvokeRoutine(string methodName, float time)
    {
        yield return new WaitForSeconds(time);
        var method = GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
        if (method == null) Debug.LogError($"Invoke: {GetType().Name} has no method '{methodName}'.");
        else method.Invoke(this, null);
    }

    // Called by the Scene once per frame, after Update.
    internal void TickCoroutines()
    {
        foreach (var state in _coroutines.ToList())
        {
            if (!_coroutines.Contains(state)) continue;
            if (!IsReady(state)) continue;
            if (!Advance(state)) Finish(state);
        }
    }

    private static bool IsReady(CoroutineState state) => state.Waiting switch
    {
        null => true,
        WaitForSeconds => Time.time >= state.WaitUntilTime,
        WaitForSecondsRealtime => Time.unscaledTime >= state.WaitUntilTime,
        WaitUntil w => w.Predicate(),
        WaitWhile w => !w.Predicate(),
        Coroutine c => c.IsDone,
        _ => true,
    };

    private bool Advance(CoroutineState state)
    {
        bool more;
        try { more = state.Routine.MoveNext(); }
        catch (Exception e)
        {
            Debug.LogError($"Coroutine in {GetType().Name} on '{name}': {e.GetType().Name}: {e.Message}{SourceLocation(e)}");
            return false;
        }
        if (!more) return false;
        state.Waiting = state.Routine.Current;
        if (state.Waiting is WaitForSeconds w) state.WaitUntilTime = Time.time + w.Seconds;
        if (state.Waiting is WaitForSecondsRealtime r) state.WaitUntilTime = Time.unscaledTime + r.Seconds;
        return true;
    }

    private void Finish(CoroutineState state)
    {
        state.Handle.IsDone = true;
        _coroutines.Remove(state);
    }
}

public sealed class Coroutine
{
    public bool IsDone { get; internal set; }
}

public sealed class WaitForSeconds
{
    public readonly float Seconds;
    public WaitForSeconds(float seconds) => Seconds = seconds;
}

// Ignores Time.timeScale — keeps ticking during slow motion or pause.
public sealed class WaitForSecondsRealtime
{
    public readonly float Seconds;
    public WaitForSecondsRealtime(float seconds) => Seconds = seconds;
}

public sealed class WaitUntil
{
    public readonly Func<bool> Predicate;
    public WaitUntil(Func<bool> predicate) => Predicate = predicate;
}

public sealed class WaitWhile
{
    public readonly Func<bool> Predicate;
    public WaitWhile(Func<bool> predicate) => Predicate = predicate;
}
