namespace CodexQuotaWidget.Core;

public class CodexAppServerException : Exception
{
    public CodexAppServerException(string message)
        : base(message)
    {
    }

    public CodexAppServerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CodexAppServerProtocolException : CodexAppServerException
{
    public CodexAppServerProtocolException(string message)
        : base(message)
    {
    }

    public CodexAppServerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CodexAppServerRequestException : CodexAppServerException
{
    public CodexAppServerRequestException(string message, int? code = null)
        : base(message)
    {
        Code = code;
    }

    public int? Code { get; }
}
