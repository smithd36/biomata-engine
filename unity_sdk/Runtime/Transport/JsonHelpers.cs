// Biomata.SDK — JsonHelpers.cs
//
// JObject ↔ Dictionary<string, object> conversion. Mirrors what
// ProtoUtils.FromStruct does for protobuf — the rest of the SDK consumes
// plain BCL dictionaries, so the transport layer is the only place that
// touches Newtonsoft.Json types.

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Biomata.SDK.Transport
{
    internal static class JsonHelpers
    {
        /// <summary>
        /// Convert a JToken (Object / Array / primitive) into the same plain-BCL
        /// shape ProtoUtils.FromStruct produces: Dictionary&lt;string, object&gt;,
        /// List&lt;object&gt;, primitives, or null.
        ///
        /// Keeping both transports producing identical output shapes is what
        /// lets the rest of the SDK be transport-agnostic.
        /// </summary>
        public static object FromToken(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            switch (token.Type)
            {
                case JTokenType.Object:
                    return FromObject((JObject)token);
                case JTokenType.Array:
                    return FromArray((JArray)token);
                case JTokenType.Integer:
                    // Match ProtoUtils.FromStruct, which maps protobuf NumberValue
                    // to double. Keeping numeric type identical between transports
                    // means consumer code doesn't need transport-aware unboxing.
                    return (double)token.Value<long>();
                case JTokenType.Float:
                    return token.Value<double>();
                case JTokenType.String:
                    return token.Value<string>();
                case JTokenType.Boolean:
                    return token.Value<bool>();
                default:
                    return token.ToString();
            }
        }

        public static Dictionary<string, object> FromObject(JObject obj)
        {
            if (obj == null) return new Dictionary<string, object>();
            var result = new Dictionary<string, object>(obj.Count);
            foreach (var kv in obj)
                result[kv.Key] = FromToken(kv.Value);
            return result;
        }

        public static List<object> FromArray(JArray arr)
        {
            if (arr == null) return new List<object>();
            var result = new List<object>(arr.Count);
            foreach (var t in arr)
                result.Add(FromToken(t));
            return result;
        }

        /// <summary>
        /// Convert a BCL value tree (Dictionary&lt;string, object&gt;, List, primitives)
        /// to a JToken. Strings, primitives, and nulls pass through; nested
        /// dictionaries become JObjects, IEnumerables become JArrays.
        ///
        /// JsonConvert.SerializeObject does the right thing for most of this, but
        /// going through an explicit walk lets us emit identical output to the
        /// Python json.dumps the server expects (no extra wrapping objects).
        /// </summary>
        public static JToken ToToken(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is string s)  return new JValue(s);
            if (value is bool b)    return new JValue(b);
            if (value is int    i)  return new JValue(i);
            if (value is long   l)  return new JValue(l);
            if (value is float  f)  return new JValue(f);
            if (value is double d)  return new JValue(d);
            if (value is IDictionary<string, object> dict)
            {
                var obj = new JObject();
                foreach (var kv in dict) obj[kv.Key] = ToToken(kv.Value);
                return obj;
            }
            if (value is System.Collections.IEnumerable enumerable)
            {
                var arr = new JArray();
                foreach (var item in enumerable) arr.Add(ToToken(item));
                return arr;
            }
            // Fallback — let Newtonsoft serialize and parse it back. Covers
            // user-defined classes that the smoke test or app might inject.
            return JToken.FromObject(value);
        }
    }
}
