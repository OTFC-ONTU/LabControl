using System.Net;
using System.Net.Sockets;

namespace LabControl.Shared.Internet;

/// <summary>
/// The addresses the whitelist has learned from allowed answers (M6 portion 3, D-74) — what
/// the Windows side writes into the firewall's allow rule. An address stays for the whole
/// whitelist session, whatever its TTL said, because a CDN answer that changes under a long
/// download must not cut it; past <see cref="Defaults.WhitelistAddressCap"/> the least
/// recently admitted one makes room. IPv4-mapped IPv6 addresses are kept as IPv4; loopback,
/// unspecified, broadcast and multicast addresses are never kept (an answer naming them would
/// open nothing useful and could only widen the rule). Thread-safe.
/// </summary>
public sealed class AllowedAddressSet
{
    private readonly object _gate = new();
    private readonly LinkedList<IPAddress> _order = new();
    private readonly Dictionary<IPAddress, LinkedListNode<IPAddress>> _index = [];
    private readonly int _cap;

    /// <param name="cap">How many addresses are kept; <see cref="Defaults.WhitelistAddressCap"/> by default.</param>
    public AllowedAddressSet(int? cap = null)
    {
        _cap = Math.Max(1, cap ?? Defaults.WhitelistAddressCap);
    }

    /// <summary>How many addresses the set holds.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _order.Count;
            }
        }
    }

    /// <summary>
    /// Adds <paramref name="addresses"/>, making each the most recently admitted. Returns
    /// <c>true</c> when the membership changed (an address came in or one was pushed out), so
    /// the caller rewrites the rule only then; re-admitting a known address returns <c>false</c>.
    /// </summary>
    public bool Admit(IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var changed = false;
        lock (_gate)
        {
            foreach (var raw in addresses)
            {
                var address = Canonical(raw);
                if (address is null)
                {
                    continue;
                }

                if (_index.TryGetValue(address, out var node))
                {
                    _order.Remove(node);
                    _order.AddLast(node);
                    continue;
                }

                _index[address] = _order.AddLast(address);
                changed = true;

                while (_order.Count > _cap)
                {
                    var oldest = _order.First!;
                    _order.RemoveFirst();
                    _index.Remove(oldest.Value);
                }
            }
        }

        return changed;
    }

    /// <summary>The addresses, the least recently admitted first.</summary>
    public IReadOnlyList<IPAddress> Snapshot()
    {
        lock (_gate)
        {
            return _order.ToArray();
        }
    }

    /// <summary>Forgets every address, as when a whitelist session ends.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _order.Clear();
            _index.Clear();
        }
    }

    /// <summary>
    /// The form an address is kept in, or <c>null</c> when it is never kept: IPv4-mapped IPv6
    /// becomes IPv4, an IPv6 scope id is dropped, and loopback, unspecified, broadcast and
    /// multicast addresses are refused.
    /// </summary>
    public static IPAddress? Canonical(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6Multicast || address.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(address))
            {
                return null;
            }

            return address.ScopeId == 0 ? address : new IPAddress(address.GetAddressBytes());
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var bytes = address.GetAddressBytes();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) || (bytes[0] & 0xF0) == 0xE0)
        {
            return null;
        }

        return address;
    }
}
