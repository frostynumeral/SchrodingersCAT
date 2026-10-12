namespace SchrodingersCat.Memory;

/// <summary>A termbase file (or an entry stored in it) is damaged, or from an incompatible version.</summary>
public sealed class TermbaseFormatException : Exception
{
    public TermbaseFormatException()
    {
    }

    public TermbaseFormatException(string message)
        : base(message)
    {
    }

    public TermbaseFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
