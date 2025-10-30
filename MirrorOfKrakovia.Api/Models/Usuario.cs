using MongoDB.Bson.Serialization.Attributes;

namespace MirrorOfKrakovia.Api.Models
{
    public class Usuario
    {
        [BsonId]
        [BsonRepresentation(MongoDB.Bson.BsonType.String)]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [BsonElement("usuario")]
        public string UsuarioNome { get; set; } = string.Empty;

        [BsonElement("senha")]
        public string Senha { get; set; } = string.Empty;

        [BsonElement("email")]
        public string Email { get; set; } = string.Empty;
    }
}
