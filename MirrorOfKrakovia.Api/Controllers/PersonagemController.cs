using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using MirrorOfKrakovia.Api.Models;
using MirrorOfKrakovia.Api.Services;

namespace MirrorOfKrakovia.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PersonagemController : ControllerBase
    {
        private readonly IMongoCollection<Personagem> _personagens;
        private readonly IMongoCollection<Item> _itens;

        public PersonagemController(DatabaseService db)
        {
            _personagens = db.GetCollection<Personagem>("personagens");
            _itens = db.GetCollection<Item>("itens");
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var personagens = await _personagens.Find(_ => true).ToListAsync();

            var resultado = new List<object>();

            foreach (var p in personagens)
            {
                var itens = await _itens.Find(i => i.IdPersonagem == p.Id).ToListAsync();
                resultado.Add(new
                {
                    personagem = p,
                    itens
                });
            }

            return Ok(resultado);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(string id)
        {
            var personagem = await _personagens.Find(p => p.Id == id).FirstOrDefaultAsync();
            if (personagem == null) return NotFound();

            var itens = await _itens.Find(i => i.IdPersonagem == id).ToListAsync();

            return Ok(new
            {
                personagem,
                itens
            });
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Personagem personagem)
        {
            await _personagens.InsertOneAsync(personagem);
            return CreatedAtAction(nameof(GetById), new { id = personagem.Id }, personagem);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string id, [FromBody] Personagem personagem)
        {
            var result = await _personagens.ReplaceOneAsync(p => p.Id == id, personagem);
            return result.MatchedCount == 0 ? NotFound() : Ok(personagem);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var result = await _personagens.DeleteOneAsync(p => p.Id == id);
            return result.DeletedCount == 0 ? NotFound() : NoContent();
        }
    }
}
