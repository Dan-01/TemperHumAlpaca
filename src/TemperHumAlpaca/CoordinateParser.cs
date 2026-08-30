using System.Globalization;
using System.Text;

internal enum CoordinateAxis
{
    Latitude,
    Longitude
}

internal static class CoordinateParser
{
    public static double? ParseNullable(string raw, CoordinateAxis axis)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Parse(raw, axis);
    }

    public static double Parse(string raw, CoordinateAxis axis)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException($"{AxisName(axis)} is required.");
        }

        var text = raw.Trim().ToUpperInvariant();
        var hemisphere = ExtractHemisphere(text, axis);
        var cleaned = NormalizeSymbols(text);
        var parts = cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length is < 1 or > 3)
        {
            throw FormatError(axis);
        }

        if (!TryParseNumber(parts[0], out var degrees))
        {
            throw FormatError(axis);
        }

        var minutes = 0.0;
        var seconds = 0.0;

        if (parts.Length >= 2 && !TryParseNumber(parts[1], out minutes))
        {
            throw FormatError(axis);
        }

        if (parts.Length == 3 && !TryParseNumber(parts[2], out seconds))
        {
            throw FormatError(axis);
        }

        if (minutes is < 0 or >= 60)
        {
            throw new InvalidOperationException($"{AxisName(axis)} minutes must be from 0 up to (but not including) 60.");
        }

        if (seconds is < 0 or >= 60)
        {
            throw new InvalidOperationException($"{AxisName(axis)} seconds must be from 0 up to (but not including) 60.");
        }

        var explicitSign = Math.Sign(degrees);
        var magnitude = Math.Abs(degrees) + minutes / 60.0 + seconds / 3600.0;
        var hemisphereSign = HemisphereSign(hemisphere);

        if (hemisphereSign is not null && explicitSign < 0 && hemisphereSign.Value > 0)
        {
            throw new InvalidOperationException(
                $"{AxisName(axis)} has conflicting sign and hemisphere. Use either a signed value or a matching N/S/E/W direction.");
        }

        var sign = hemisphereSign ?? (degrees < 0 ? -1 : 1);
        var value = magnitude * sign;
        var limit = axis == CoordinateAxis.Latitude ? 90.0 : 180.0;

        if (Math.Abs(value) > limit + 1e-10)
        {
            throw new InvalidOperationException($"{AxisName(axis)} must be between {-limit:0} and +{limit:0} degrees.");
        }

        if (Math.Abs(degrees) >= limit && (minutes > 0 || seconds > 0))
        {
            throw new InvalidOperationException(
                $"{AxisName(axis)} cannot exceed {limit:0} degrees; minutes and seconds must be zero at the limit.");
        }

        return value;
    }

    private static char? ExtractHemisphere(string text, CoordinateAxis axis)
    {
        var directions = text
            .Where(character => character is 'N' or 'S' or 'E' or 'W')
            .ToArray();

        if (directions.Length > 1)
        {
            throw new InvalidOperationException($"{AxisName(axis)} must contain at most one N/S/E/W hemisphere letter.");
        }

        if (directions.Length == 0)
        {
            return null;
        }

        var direction = directions[0];
        var valid = axis == CoordinateAxis.Latitude
            ? direction is 'N' or 'S'
            : direction is 'E' or 'W';

        if (!valid)
        {
            var expected = axis == CoordinateAxis.Latitude ? "N or S" : "E or W";
            throw new InvalidOperationException($"{AxisName(axis)} hemisphere must be {expected}.");
        }

        return direction;
    }

    private static string NormalizeSymbols(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is 'N' or 'S' or 'E' or 'W')
            {
                builder.Append(' ');
                continue;
            }

            if (character is '°' or 'º' or '˚' or '\'' or '′' or '’' or '"' or '″' or '”' or ':')
            {
                builder.Append(' ');
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                builder.Append(' ');
                continue;
            }

            if (char.IsDigit(character) || character is '+' or '-' or '.')
            {
                builder.Append(character);
                continue;
            }

            throw new InvalidOperationException(
                "Coordinates may contain numbers, decimal points, degree/minute/second symbols, spaces, colons and N/S/E/W hemisphere letters.");
        }

        return builder.ToString();
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value);

    private static int? HemisphereSign(char? hemisphere) => hemisphere switch
    {
        'N' or 'E' => 1,
        'S' or 'W' => -1,
        _ => null
    };

    private static string AxisName(CoordinateAxis axis) =>
        axis == CoordinateAxis.Latitude ? "Latitude" : "Longitude";

    private static InvalidOperationException FormatError(CoordinateAxis axis) =>
        new(
            $"{AxisName(axis)} must be decimal degrees or degrees/minutes/seconds, for example " +
            (axis == CoordinateAxis.Latitude
                ? "51.6367 or 51°38'12\"N."
                : "-0.3625 or 0°21'45\"W."));
}
