using System.Text;

namespace Juvin.Core.Providers.Streaming;

/// <summary>
///     Removes <c>&lt;think&gt;…&lt;/think&gt;</c> reasoning blocks from streamed text; tags may be
///     split across deltas.
/// </summary>
internal sealed class ThinkingBlockStreamFilter
{
    private const string OpenTag = "<think>";
    private const string CloseTag = "</think>";

    private string _pending = "";
    private bool _inside;
    private bool _hasVisibleText;
    private bool _trimLeadingWhitespace = true;

    public string Push(string delta)
    {
        var text = _pending + delta;
        _pending = "";
        var visible = new StringBuilder();
        var position = 0;
        while (position < text.Length)
        {
            var tag = _inside ? CloseTag : OpenTag;
            var remaining = text.AsSpan(position);
            if (remaining.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
            {
                _inside = !_inside;
                if (!_inside && !_hasVisibleText)
                {
                    _trimLeadingWhitespace = true;
                }

                position += tag.Length;
                continue;
            }

            // Hold a possible tag prefix until the next delta disambiguates it, so reasoning
            // never leaks into the output.
            if (
                (
                    remaining.Length < OpenTag.Length
                    && OpenTag.AsSpan().StartsWith(remaining, StringComparison.OrdinalIgnoreCase)
                )
                || (
                    remaining.Length < CloseTag.Length
                    && CloseTag.AsSpan().StartsWith(remaining, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                _pending = text[position..];
                break;
            }

            var character = text[position++];
            if (_inside || (_trimLeadingWhitespace && char.IsWhiteSpace(character)))
            {
                continue;
            }

            _trimLeadingWhitespace = false;
            _hasVisibleText = true;
            visible.Append(character);
        }

        return visible.ToString();
    }

    public string Flush()
    {
        var remaining = _inside ? "" : _pending;
        _pending = "";
        if (_trimLeadingWhitespace)
        {
            remaining = remaining.TrimStart();
        }

        if (remaining.Length == 0)
        {
            return remaining;
        }

        _hasVisibleText = true;
        _trimLeadingWhitespace = false;
        return remaining;
    }
}
