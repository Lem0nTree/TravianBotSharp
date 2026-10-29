namespace MainCore.Parsers
{
    public static class BuildingNameParser
    {
        public static bool TryParse(string? name, out BuildingEnums type)
        {
            // Display names contain spaces and apostrophes that enum names omit.
            string normalizedName = new(name?.Where(char.IsLetterOrDigit).ToArray() ?? []);

            type = BuildingEnums.Unknown;
            return normalizedName.Any(char.IsLetter)
                && Enum.TryParse(normalizedName, ignoreCase: true, out type)
                && Enum.IsDefined(type);
        }

        public static BuildingEnums Parse(string name)
        {
            if (TryParse(name, out var type)) return type;

            throw new ArgumentException($"Unknown building name: {name}", nameof(name));
        }
    }
}
