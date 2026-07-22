namespace FluentShell.Core;

public sealed class SessionCoordinator<TSession> where TSession : class
{
    private readonly Func<TSession, Guid> _keySelector;
    private readonly Dictionary<Guid, TSession> _sessions = [];
    private readonly List<TSession> _order = [];
    private readonly HashSet<Guid> _connecting = [];

    public SessionCoordinator(Func<TSession, Guid> keySelector)
    {
        _keySelector = keySelector;
    }

    public int Count => _sessions.Count;
    public TSession? Selected { get; private set; }
    public IReadOnlyCollection<TSession> Sessions => _sessions.Values;

    public bool TryGet(Guid key, out TSession session) =>
        _sessions.TryGetValue(key, out session!);

    public bool TryBeginConnection(Guid key) => _connecting.Add(key);

    public void EndConnection(Guid key) => _connecting.Remove(key);

    public void Add(TSession session)
    {
        var key = _keySelector(session);
        if (!_sessions.TryAdd(key, session))
            throw new InvalidOperationException("同一服务器只能存在一个活动会话。");
        _order.Add(session);
    }

    public bool Contains(TSession session) =>
        _sessions.TryGetValue(_keySelector(session), out var current) &&
        ReferenceEquals(current, session);

    public void Select(TSession session)
    {
        if (!Contains(session))
            throw new InvalidOperationException("无法选择未注册的会话。");
        Selected = session;
    }

    public TSession? Remove(TSession session)
    {
        var removedIndex = _order.IndexOf(session);
        var wasSelected = ReferenceEquals(Selected, session);
        _sessions.Remove(_keySelector(session));
        _order.Remove(session);
        Selected = SessionSelection.AfterRemoval(_order, removedIndex, wasSelected, Selected);
        return Selected;
    }

    public void ClearSelection() => Selected = null;
}