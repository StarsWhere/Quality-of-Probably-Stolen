using System;
using Newtonsoft.Json;

namespace ShopAssist
{
    // JsonUtility can omit lists of types defined in dynamically loaded mod assemblies.
    // Use the game's JSON.NET copy and let each owner validate its versioned schema.
    internal static class ShopData
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MissingMemberHandling = MissingMemberHandling.Error,
            MaxDepth = 16
        };

        internal static T Decode<T>(string raw, int maxCharacters)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length > maxCharacters)
                throw new FormatException("Invalid saved data length");
            return JsonConvert.DeserializeObject<T>(raw, Settings);
        }

        internal static string Encode<T>(T value) => JsonConvert.SerializeObject(value, Settings);
    }
}
