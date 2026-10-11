using System.Text;

namespace Bsync.Samples.Tasks;

/// <summary>Business rules the server enforces in its write handler; clients may check them early for a better UI.</summary>
public static class TaskRules
{
    /// <summary>The rejection code for a task without a title. Stable: clients match on it, never on messages.</summary>
    public const string TitleRequired = "title-required";

    /// <summary>The URL-friendly form of a title, for example <c>"Buy milk!"</c> becomes <c>"buy-milk"</c>.</summary>
    public static string Slug(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var slug = new StringBuilder(title.Length);
        foreach (var c in title.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().TrimEnd('-');
    }
}
