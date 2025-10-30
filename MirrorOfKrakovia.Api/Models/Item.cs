using MongoDB.Bson.Serialization.Attributes;

namespace MirrorOfKrakovia.Api.Models
{
    public class Item
    {
        [BsonId]
        [BsonRepresentation(MongoDB.Bson.BsonType.String)]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [BsonElement("id_personagem")]
        public string IdPersonagem { get; set; } = string.Empty;

        [BsonElement("tipo")]
        public string Tipo { get; set; } = string.Empty;

        [BsonElement("dano")]
        public double Dano { get; set; } = 0;

        [BsonElement("vida")]
        public double Vida { get; set; } = 0;

        [BsonElement("armadura")]
        public double Armadura { get; set; } = 0;

        [BsonElement("velocidade")]
        public double Velocidade { get; set; } = 0;

        [BsonElement("ilvl")]
        public int Ilvl { get; set; } = 1;

        [BsonElement("spriteName")]
        public string SpriteName { get; set; } = string.Empty;

        [BsonElement("itemName")]
        public string ItemName { get; set; } = string.Empty;

        [BsonElement("status")]
        public string? Status { get; set; }

        [BsonElement("corrupted")]
        public bool Corrupted { get; set; } = false;
    }
}
