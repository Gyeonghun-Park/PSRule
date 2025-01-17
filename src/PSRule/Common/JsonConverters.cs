﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using PSRule.Data;
using PSRule.Pipeline;
using PSRule.Resources;
using PSRule.Runtime;

namespace PSRule
{
    /// <summary>
    /// A custom serializer to correctly convert PSObject properties to JSON instead of CLIXML.
    /// </summary>
    internal sealed class PSObjectJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(PSObject);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (!(value is PSObject obj))
                throw new ArgumentException(message: PSRuleResources.SerializeNullPSObject, paramName: nameof(value));

            if (WriteFileSystemInfo(writer, value, serializer) || WriteBaseObject(writer, obj, serializer))
                return;

            writer.WriteStartObject();
            foreach (var property in obj.Properties)
            {
                // Ignore properties that are not readable or can cause race condition
                if (!property.IsGettable || property.Value is PSDriveInfo || property.Value is ProviderInfo || property.Value is DirectoryInfo)
                    continue;

                writer.WritePropertyName(property.Name);
                serializer.Serialize(writer, property.Value);
            }
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            // Create target object based on JObject
            var result = existingValue as PSObject ?? new PSObject();

            // Read tokens
            ReadObject(value: result, reader: reader);
            return result;
        }

        private void ReadObject(PSObject value, JsonReader reader)
        {
            if (reader.TokenType != JsonToken.StartObject)
                throw new PipelineSerializationException(PSRuleResources.ReadJsonFailed);

            reader.Read();
            string name = null;

            // Read each token
            while (reader.TokenType != JsonToken.EndObject)
            {
                switch (reader.TokenType)
                {
                    case JsonToken.PropertyName:
                        name = reader.Value.ToString();
                        break;

                    case JsonToken.StartObject:
                        var child = new PSObject();
                        ReadObject(value: child, reader: reader);
                        value.Properties.Add(new PSNoteProperty(name: name, value: child));
                        break;

                    case JsonToken.StartArray:
                        var items = new List<PSObject>();
                        reader.Read();
                        var item = new PSObject();

                        while (reader.TokenType != JsonToken.EndArray)
                        {
                            ReadObject(value: item, reader: reader);
                            items.Add(item);
                            reader.Read();
                        }

                        value.Properties.Add(new PSNoteProperty(name: name, value: items.ToArray()));
                        break;

                    case JsonToken.Comment:
                        break;

                    default:
                        value.Properties.Add(new PSNoteProperty(name: name, value: reader.Value));
                        break;
                }
                reader.Read();
            }
        }

        /// <summary>
        /// Serialize a file system info object.
        /// </summary>
        private static bool WriteFileSystemInfo(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (!(value is FileSystemInfo fileSystemInfo))
                return false;

            serializer.Serialize(writer, fileSystemInfo.FullName);
            return true;
        }

        /// <summary>
        /// Serialize the base object.
        /// </summary>
        private static bool WriteBaseObject(JsonWriter writer, PSObject value, JsonSerializer serializer)
        {
            if (value.BaseObject == null || value.HasNoteProperty())
                return false;

            serializer.Serialize(writer, value.BaseObject);
            return true;
        }
    }

    /// <summary>
    /// A custom serializer to convert PSObjects that may or maynot be in a JSON array to an a PSObject array.
    /// </summary>
    internal sealed class PSObjectArrayJsonConverter : JsonConverter
    {
        private readonly TargetSourceInfo _SourceInfo;

        public PSObjectArrayJsonConverter(TargetSourceInfo sourceInfo)
        {
            _SourceInfo = sourceInfo;
        }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(PSObject[]);
        }

        public override bool CanWrite => false;

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType != JsonToken.StartObject && reader.TokenType != JsonToken.StartArray)
                throw new PipelineSerializationException(PSRuleResources.ReadJsonFailed);

            var result = new List<PSObject>();
            var isArray = reader.TokenType == JsonToken.StartArray;

            if (isArray)
                reader.Read();

            while (reader.TokenType != JsonToken.None && (!isArray || (isArray && reader.TokenType != JsonToken.EndArray)))
            {
                var value = ReadObject(reader, bindTargetInfo: true, _SourceInfo);
                result.Add(value);

                // Consume the EndObject token
                reader.Read();
            }
            return result.ToArray();
        }

        private static PSObject ReadObject(JsonReader reader, bool bindTargetInfo, TargetSourceInfo sourceInfo)
        {
            if (reader.TokenType != JsonToken.StartObject || !reader.Read())
                throw new PipelineSerializationException(PSRuleResources.ReadJsonFailed);

            var result = new PSObject();
            string name = null;
            var lineNumber = 0;
            var linePosition = 0;

            if (bindTargetInfo && reader is IJsonLineInfo lineInfo && lineInfo.HasLineInfo())
            {
                lineNumber = lineInfo.LineNumber;
                linePosition = lineInfo.LinePosition;
            }

            // Read each token
            while (reader.TokenType != JsonToken.EndObject)
            {
                switch (reader.TokenType)
                {
                    case JsonToken.PropertyName:
                        name = reader.Value.ToString();
                        if (name == PSRuleTargetInfo.PropertyName)
                        {
                            var targetInfo = ReadInfo(reader);
                            if (targetInfo != null)
                                result.SetTargetInfo(targetInfo);
                        }
                        break;

                    case JsonToken.StartObject:
                        var value = ReadObject(reader, bindTargetInfo: false, sourceInfo: null);
                        result.Properties.Add(new PSNoteProperty(name, value: value));
                        break;

                    case JsonToken.StartArray:
                        var items = ReadArray(reader: reader);
                        result.Properties.Add(new PSNoteProperty(name, value: items));
                        break;

                    case JsonToken.Comment:
                        break;

                    default:
                        result.Properties.Add(new PSNoteProperty(name, value: reader.Value));
                        break;
                }
                if (!reader.Read() || reader.TokenType == JsonToken.None)
                    throw new PipelineSerializationException(PSRuleResources.ReadJsonFailed);
            }
            if (bindTargetInfo)
            {
                result.UseTargetInfo(out PSRuleTargetInfo info);
                info.SetSource(sourceInfo?.File, lineNumber, linePosition);
            }
            return result;
        }

        private static PSRuleTargetInfo ReadInfo(JsonReader reader)
        {
            if (!reader.Read() || reader.TokenType == JsonToken.None || reader.TokenType != JsonToken.StartObject)
                return null;

            var s = JsonSerializer.Create();
            return s.Deserialize<PSRuleTargetInfo>(reader);
        }

        private static PSObject[] ReadArray(JsonReader reader)
        {
            if (reader.TokenType != JsonToken.StartArray || !reader.Read())
                throw new PipelineSerializationException(PSRuleResources.ReadJsonFailed);

            var result = new List<PSObject>();

            // Read until the end of the array
            while (reader.TokenType != JsonToken.EndArray)
            {
                switch (reader.TokenType)
                {
                    case JsonToken.StartObject:
                        result.Add(ReadObject(reader, bindTargetInfo: false, sourceInfo: null));
                        break;

                    case JsonToken.StartArray:
                        result.Add(PSObject.AsPSObject(ReadArray(reader)));
                        break;

                    case JsonToken.Null:
                        result.Add(null);
                        break;

                    case JsonToken.Comment:
                        break;

                    default:
                        result.Add(PSObject.AsPSObject(reader.Value));
                        break;
                }
                if (!reader.Read() || reader.TokenType == JsonToken.None)
                    throw new PipelineSerializationException(PSRuleResources.ReadJsonFailed);
            }
            return result.ToArray();
        }
    }

    /// <summary>
    /// A custom serializer to convert ErrorCategory to a string.
    /// </summary>
    internal sealed class ErrorCategoryJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(ErrorCategory);
        }

        public override bool CanWrite => true;

        public override bool CanRead => false;

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue(Enum.GetName(typeof(ErrorCategory), value));
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// A contract resolver to sort properties alphabetically
    /// </summary>
    internal sealed class SortedPropertyContractResolver : DefaultContractResolver
    {
        protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
        {
            return base
                .CreateProperties(type, memberSerialization)
                .OrderBy(prop => prop.PropertyName)
                .ToList();
        }
    }
}
