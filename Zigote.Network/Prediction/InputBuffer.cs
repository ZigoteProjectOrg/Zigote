namespace Zigote.Network;

/// <summary>
///     Ordered ring of un-acknowledged client inputs. The client keeps inputs here after sending them so it
///     can replay the still-unacked ones when the server's authoritative state arrives. Acking by sequence
///     drops everything the server has already processed.
/// </summary>
public sealed class InputBuffer<TInput> where TInput : IInputCommand
{
    private readonly List<TInput> _inputs = [];

    public int Count => _inputs.Count;
    public IReadOnlyList<TInput> Pending => _inputs;

    public void Add(TInput input)
    {
        _inputs.Add(input);
    }

    /// <summary>Drop every buffered input with a sequence at or below <paramref name="sequence" />.</summary>
    public void AckThrough(uint sequence)
    {
        var keep = 0;
        while (keep < _inputs.Count && _inputs[keep].Sequence <= sequence) keep++;
        if (keep > 0) _inputs.RemoveRange(0, keep);
    }

    public void Clear()
    {
        _inputs.Clear();
    }
}