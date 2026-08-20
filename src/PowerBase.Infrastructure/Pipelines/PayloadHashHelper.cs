using System;
using System.Security.Cryptography;
using System.Text;

namespace PowerBase.Infrastructure.Pipelines;

public static class PayloadHashHelper
{
    public static byte[] ComputeHash(string payloadJson)
    {
        var bytes = Encoding.UTF8.GetBytes(payloadJson ?? string.Empty);
        return SHA256.HashData(bytes);
    }
}
