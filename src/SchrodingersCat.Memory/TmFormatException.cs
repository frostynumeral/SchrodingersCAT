namespace SchrodingersCat.Memory;

/// <summary>A TM file (or a stored entry in it) is damaged, or from an incompatible version.</summary>
public sealed class TmFormatException : Exception
{
    public TmFormatException()
    {
    }

    public TmFormatException(string message)
        : base(message)
    {
    }

    public TmFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
