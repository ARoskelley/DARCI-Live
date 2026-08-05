#nullable enable

using Neo4j.Driver;

namespace Darci.Memory.Graph;

/// <summary>
/// Runs a write inside a transaction function and CONSUMES the result before returning.
///
/// <para>Returning the <see cref="IResultCursor"/> itself is obsolete in the driver, and for a good reason
/// rather than a stylistic one: the cursor is backed by the transaction, which is committed and closed on
/// the way out, so anything read from it afterwards is reading a dead handle. Consuming inside the delegate
/// also forces the write to complete before the transaction commits.</para>
/// </summary>
public static class Neo4jWrite
{
    public static async Task<bool> RunWriteAsync(IAsyncQueryRunner tx, string cypher, object? parameters = null)
    {
        var cursor = parameters is null
            ? await tx.RunAsync(cypher)
            : await tx.RunAsync(cypher, parameters);

        await cursor.ConsumeAsync();
        return true;
    }
}
