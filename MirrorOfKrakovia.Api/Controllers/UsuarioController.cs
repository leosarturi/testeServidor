using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using MirrorOfKrakovia.Api.Models;
using MirrorOfKrakovia.Api.Services;

namespace MirrorOfKrakovia.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UsuarioController : ControllerBase
    {
        private readonly IMongoCollection<Usuario> _usuarios;
        private readonly IMongoCollection<Personagem> _personagens;

        public UsuarioController(DatabaseService db)
        {
            _usuarios = db.GetCollection<Usuario>("usuarios");
            _personagens = db.GetCollection<Personagem>("personagens");
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var usuarios = await _usuarios.Find(_ => true).ToListAsync();

            var resultado = new List<object>();

            foreach (var u in usuarios)
            {
                var personagens = await _personagens.Find(p => p.IdUsuario == u.Id).ToListAsync();
                resultado.Add(new
                {
                    usuario = u,
                    personagens
                });
            }

            return Ok(resultado);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(string id)
        {
            var usuario = await _usuarios.Find(u => u.Id == id).FirstOrDefaultAsync();
            if (usuario == null) return NotFound();

            var personagens = await _personagens.Find(p => p.IdUsuario == id).ToListAsync();

            return Ok(new
            {
                usuario,
                personagens
            });
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Usuario usuario)
        {
            await _usuarios.InsertOneAsync(usuario);
            return CreatedAtAction(nameof(GetById), new { id = usuario.Id }, usuario);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string id, [FromBody] Usuario usuario)
        {
            var result = await _usuarios.ReplaceOneAsync(u => u.Id == id, usuario);
            return result.MatchedCount == 0 ? NotFound() : Ok(usuario);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var result = await _usuarios.DeleteOneAsync(u => u.Id == id);
            return result.DeletedCount == 0 ? NotFound() : NoContent();
        }
    }
}
