namespace Wtyczka;

internal sealed class BaseLinkerException : Exception
{
    public BaseLinkerException(string message) : base(message)
    {
    }

    public BaseLinkerException(string message, Exception? inner) : base(message, inner)
    {
    }
}

internal sealed class SubiektIntegrationException : Exception
{
    public SubiektIntegrationException(string message) : base(message)
    {
    }

    public SubiektIntegrationException(string message, Exception? inner) : base(message, inner)
    {
    }
}
