namespace Enactive.Engine.Tests;

using System.Collections;
using System.Reflection;
using Enactive.Settings;
using Xunit;

/// <summary>
/// Everything in the settings survives <see cref="AppSettings.Clone"/>.
///
/// <para><b>Why this is worth a test of its own.</b> The settings window does not edit the live
/// settings: it takes a clone, edits that, and on Save writes THAT OBJECT over the file and puts
/// it in place of the live one. So a property <c>Clone</c> forgets is not merely missing from the
/// window — it is reset to its default the next time anybody presses Save, from any pane, whether
/// or not they went near it.</para>
///
/// <para>Reported 2026-09-22: the SMTP section was filled in and came back empty. <c>Smtp</c> had
/// been added to the settings and not to <c>Clone</c>, so the window showed blanks over a file
/// that had the account in it, and saving wrote the blanks down. <c>KeepRuns</c> and
/// <c>ProposeChecks</c> were being erased the same way and nobody had noticed, which is the point:
/// the failure is silent, and it arrives with every new section somebody adds.</para>
///
/// <para>So the test is written against the TYPE rather than against a list of properties: it
/// fills every public settable property with a non-default value, clones, and compares the two
/// objects property by property, including the ones <c>[JsonIgnore]</c> keeps off disk — a
/// password that is not carried across is the same bug wearing a different hat.</para>
/// </summary>
public sealed class SettingsSurviveTheEditorTests
{
    [Fact]
    public void Every_setting_survives_a_clone()
    {
        var settings = new AppSettings();
        Fill(settings, depth: 0);

        var difference = FirstDifference(settings, settings.Clone(), "AppSettings", depth: 0);

        Assert.True(difference is null,
            $"Clone() dropped {difference}. Every settable property has to be copied there: the "
            + "settings window saves the clone over the file, so what Clone forgets, Save erases.");
    }

    /// <summary>
    /// The one that would have caught it on its own, in the words of the report: fill the section
    /// in, clone as the window does, and the account is still there.
    /// </summary>
    [Fact]
    public void The_mail_account_is_still_there_after_the_window_copies_the_settings()
    {
        var settings = new AppSettings();
        settings.Smtp.Host = "smtp.example.com";
        settings.Smtp.Port = 2525;
        settings.Smtp.User = "someone@example.com";
        settings.Smtp.Password = "not-on-disk";
        settings.Smtp.Recipients.Add("me@example.com");

        var copy = settings.Clone();

        Assert.Equal("smtp.example.com", copy.Smtp.Host);
        Assert.Equal(2525, copy.Smtp.Port);
        Assert.Equal("someone@example.com", copy.Smtp.User);
        Assert.Equal("not-on-disk", copy.Smtp.Password);
        Assert.Equal(new[] { "me@example.com" }, copy.Smtp.Recipients);

        // A copy, not the same list: the editor must be discardable on Cancel.
        Assert.NotSame(settings.Smtp, copy.Smtp);
        Assert.NotSame(settings.Smtp.Recipients, copy.Smtp.Recipients);
    }

    private const int MaxDepth = 4;

    /// <summary>Gives every settable property a value nothing would have by default.</summary>
    private static void Fill(object target, int depth)
    {
        if (depth > MaxDepth) return;

        foreach (var property in Settable(target.GetType()))
        {
            var type = property.PropertyType;
            var under = Nullable.GetUnderlyingType(type) ?? type;

            if (under == typeof(string))
                property.SetValue(target, "filled-" + property.Name);
            else if (under == typeof(bool))
                property.SetValue(target, !(bool)(property.GetValue(target) ?? false));
            else if (under == typeof(int))
                property.SetValue(target, 4242);
            else if (under == typeof(long))
                property.SetValue(target, 4242L);
            else if (under == typeof(double))
                property.SetValue(target, 42.5);
            else if (under.IsEnum)
                property.SetValue(target, Enum.GetValues(under).GetValue(
                    Enum.GetValues(under).Length - 1));
            else if (IsList(under, out var element))
                property.SetValue(target, OneItemList(under, element!, depth));
            else if (under.IsClass && under.GetConstructor(Type.EmptyTypes) is not null)
            {
                var value = property.GetValue(target) ?? Activator.CreateInstance(under)!;
                Fill(value, depth + 1);
                property.SetValue(target, value);
            }
        }
    }

    private static object OneItemList(Type listType, Type element, int depth)
    {
        var list = (IList)Activator.CreateInstance(listType)!;

        if (element == typeof(string))
            list.Add("filled-item");
        else if (element.GetConstructor(Type.EmptyTypes) is not null)
        {
            var item = Activator.CreateInstance(element)!;
            Fill(item, depth + 1);
            list.Add(item);
        }

        return list;
    }

    /// <summary>The path of the first property that did not come across, or null.</summary>
    private static string? FirstDifference(object left, object right, string path, int depth)
    {
        if (depth > MaxDepth) return null;

        foreach (var property in Settable(left.GetType()))
        {
            var here = $"{path}.{property.Name}";
            var a = property.GetValue(left);
            var b = property.GetValue(right);

            if (a is null || b is null)
            {
                if (!ReferenceEquals(a, b)) return here;
                continue;
            }

            var under = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (under.IsPrimitive || under.IsEnum || under == typeof(string) || under == typeof(decimal))
            {
                if (!Equals(a, b)) return here;
            }
            else if (a is IList first && b is IList second)
            {
                if (first.Count != second.Count) return here;
                for (var i = 0; i < first.Count; i++)
                {
                    if (first[i] is null || second[i] is null)
                    {
                        if (!ReferenceEquals(first[i], second[i])) return $"{here}[{i}]";
                        continue;
                    }

                    if (first[i] is string)
                    {
                        if (!Equals(first[i], second[i])) return $"{here}[{i}]";
                    }
                    else if (FirstDifference(first[i]!, second[i]!, $"{here}[{i}]", depth + 1) is { } inner)
                        return inner;
                }
            }
            else if (FirstDifference(a, b, here, depth + 1) is { } nested)
                return nested;
        }

        return null;
    }

    private static IEnumerable<PropertyInfo> Settable(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
               .Where(p => p.CanRead && p.CanWrite && p.SetMethod?.IsPublic == true)
               // An indexer is not a setting; it is how a collection is read.
               .Where(p => p.GetIndexParameters().Length == 0)
               // Set by Save() and by the loader, about one file rather than about the settings.
               .Where(p => p.Name != nameof(AppSettings.LastSaveError));

    private static bool IsList(Type type, out Type? element)
    {
        element = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
            ? type.GetGenericArguments()[0]
            : null;
        return element is not null;
    }
}
