// SPDX-License-Identifier: GPL-3.0-or-later
//
// The delve records the server has sent (0xD8), by kind and id. Retail keeps them too (in a file, an hour each); OpenDAoC
// answers each request once an hour per connection, so a client that forgot one couldn't get it back. Held by the session,
// so it outlives a region change's new world view.

using DaocProtocol.Packets;

namespace DaocProtocol.World;

public sealed class DelveCache
{
    private readonly Dictionary<(string, ushort), DelveRecord> _records = [];
    private readonly HashSet<(ushort, ushort)> _asked = [];

    public int Count => _records.Count;

    public void Add(DelveRecord record) => _records[(record.Kind, record.Index)] = record;

    public DelveRecord? Get(string kind, ushort index) => _records.GetValueOrDefault((kind, index));

    /// <summary>Whether to ask for (request type, id): true the first time only, so each is asked once.</summary>
    public bool ShouldAsk(ushort type, ushort id) => _asked.Add((type, id));
}
