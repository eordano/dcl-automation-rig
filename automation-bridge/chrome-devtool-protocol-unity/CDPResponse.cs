using System;
using System.Text;
using Newtonsoft.Json;
using REnum;

namespace CDPBridges
{
    /// <summary>
    /// T should be "object", not a stringified JSON
    /// </summary>
    [Serializable]
    public struct CDPResponseRaw<T> where T : struct
    {
        public int id;
        public T result;

        public CDPResponseRaw(int id, T result)
        {
            this.id = id;
            this.result = result;
        }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    public readonly struct CDPResponse
    {
        public readonly int Id;
        public readonly CDPResult Result;

        public CDPResponse(int id, CDPResult result)
        {
            Id = id;
            Result = result;
        }

        public string ToJson()
        {
            if (Result.IsJson(out CDPResult.Json json))
                return new StringBuilder(json.raw.Length + 28)
                    .Append("{\"id\":").Append(Id).Append(",\"result\":").Append(json.raw).Append('}')
                    .ToString();

            if (Result.IsGetResponseBody(out CDPResult.GetResponseBody body))
                return new CDPResponseRaw<CDPResult.GetResponseBody>(Id, body).ToJson();

            return new CDPResponseRaw<CDPResult.Empty>().ToJson();
        }

        public override string ToString()
        {
            return $"({nameof(CDPResponse)} {{ id: {Id}, method: {Result.ToString()} }})";
        }
    }

    [REnum]
    [REnumFieldEmpty("Network_enable")]
    [REnumField(typeof(GetResponseBody))]
    [REnumField(typeof(Json))]
    public partial struct CDPResult
    {
        [Serializable]
        public struct Empty
        {
        }
        
        [Serializable]
        public struct GetResponseBody
        {
            public string body;
            public bool base64Encoded;

            public GetResponseBody(string body, bool base64Encoded)
            {
                this.body = body;
                this.base64Encoded = base64Encoded;
            }
        }

        public readonly struct Json
        {
            public readonly string raw;

            public Json(string raw)
            {
                this.raw = raw;
            }
        }
    }
}