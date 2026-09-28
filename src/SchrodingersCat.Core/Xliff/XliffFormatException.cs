namespace SchrodingersCat.Core.Xliff;

/// <summary>The XLIFF content is malformed or uses a feature that is not supported (yet).</summary>
public sealed class XliffFormatException : Exception
{
    public XliffFormatException(string message) : base(message)
    {
    }

    public XliffFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
