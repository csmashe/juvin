using System.Globalization;
using System.Net.Sockets;

namespace Juvin.App.Services.CrashReporting;

/// <summary>Tags a report with the facts an exception's redacted message would have carried: HRESULT, innermost type, HTTP status, socket error.</summary>
internal static class CrashReportErrorFacts
{
    public const string HResultTag = "error.hresult";
    public const string InnerTypeTag = "error.inner_type";
    public const string HttpStatusTag = "http.status";
    public const string SocketErrorTag = "socket.error";

    public static void Apply(SentryEvent e)
    {
        if (e.Exception is not { } outermost)
        {
            return;
        }

        string? httpStatus = null;
        string? socketError = null;
        Exception? innermost = null;
        var innermostDepth = -1;
        Walk(outermost, 0);

        e.SetTag(HResultTag, $"0x{outermost.HResult:X8}");
        if (httpStatus is not null)
        {
            e.SetTag(HttpStatusTag, httpStatus);
        }

        if (socketError is not null)
        {
            e.SetTag(SocketErrorTag, socketError);
        }

        if (innermost is not null && innermost.GetType() != outermost.GetType() && innermost.GetType().FullName is { } innerType)
        {
            e.SetTag(InnerTypeTag, innerType);
        }

        return;

        void Walk(Exception? exception, int depth)
        {
            while (exception is not null && depth < 32)
            {
                if (httpStatus is null && exception is HttpRequestException { StatusCode: { } status })
                {
                    httpStatus = ((int)status).ToString(CultureInfo.InvariantCulture);
                }

                if (socketError is null && exception is SocketException socket)
                {
                    socketError = socket.SocketErrorCode.ToString();
                }

                if (exception is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        Walk(inner, depth + 1);
                    }

                    return;
                }

                if (depth > innermostDepth)
                {
                    innermost = exception;
                    innermostDepth = depth;
                }

                exception = exception.InnerException;
                depth++;
            }
        }
    }
}
