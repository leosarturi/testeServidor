using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace MirrorOfKrakovia.Api.Models
{
    public class Personagem
    {
        [BsonId]
        [BsonRepresentation(MongoDB.Bson.BsonType.String)]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [BsonElement("id_usuario")]
        [JsonPropertyName("id_usuario")] // ✅ faz o binding funcionar
        public string IdUsuario { get; set; } = string.Empty;

        [BsonElement("nick")]
        public string Nick { get; set; } = string.Empty;

        [BsonElement("classe")]
        public string Classe { get; set; } = string.Empty;

        [BsonElement("vida_maxima")]
        public double VidaMaxima { get; set; } = 100;

        [BsonElement("dano")]
        public double Dano { get; set; } = 10;

        [BsonElement("armadura")]
        public double Armadura { get; set; } = 0;

        [BsonElement("velocidade")]
        public double Velocidade { get; set; } = 1;

        [BsonElement("exp")]
        public double Exp { get; set; } = 0;

        [BsonElement("level")]
        public int Level { get; set; } = 1;

        [BsonElement("slots")]
        public string[] Slots { get; set; } = new string[5];

        [BsonElement("chaos")]
        public int Chaos { get; set; } = 0;

        [BsonElement("vaal")]
        public int Vaal { get; set; } = 0;

        [BsonElement("mirror")]
        public int Mirror { get; set; } = 0;

        [BsonElement("currentQuest")]
        public string? CurrentQuest { get; set; }

        [BsonElement("boss1")]
        public bool Boss1 { get; set; } = false;

        [BsonElement("boss2")]
        public bool Boss2 { get; set; } = false;

        [BsonElement("boss3")]
        public bool Boss3 { get; set; } = false;

        [BsonElement("boss4")]
        public bool Boss4 { get; set; } = false;

        [BsonElement("boss5")]
        public bool Boss5 { get; set; } = false;
    }
}
