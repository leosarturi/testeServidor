using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;

namespace MirrorOfKrakovia.Api.Models
{
    public class Usuario
    {
        [BsonId]
        [BsonRepresentation(MongoDB.Bson.BsonType.String)]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [BsonElement("usuario")]
        [JsonPropertyName("usuario")]
        public string UsuarioNome { get; set; } = string.Empty;

        [BsonElement("senha")]
        [JsonIgnore] // nunca retornar
        public string SenhaHash { get; set; } = string.Empty;

        [BsonElement("email")]
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        // 🔐 gera hash SHA-256
        public static string HashPassword(string senha)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(senha);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }

        // 🔍 compara senha com hash
        public bool VerifyPassword(string senha)
        {
            return SenhaHash == HashPassword(senha);
        }
    }
}
