using System.Runtime.CompilerServices;

// The editor is a separate assembly but needs a few engine internals that
// game code shouldn't touch (calling OnValidate, loading a component's
// enabled flag without firing OnEnable, ...). This grants the Editor
// project — and only it — access to `internal` members.
[assembly: InternalsVisibleTo("Editor")]
