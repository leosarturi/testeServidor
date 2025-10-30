using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using MirrorOfKrakovia.Api.Models;
using MirrorOfKrakovia.Api.Services;
using MirrorOfKrakovia.Api.Models.Dtos;


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

            // ✅ cria índices únicos no Mongo
            var indexKeys = Builders<Usuario>.IndexKeys
                .Ascending(u => u.UsuarioNome)
                .Ascending(u => u.Email);

            var indexOptions = new CreateIndexOptions { Unique = true };
            var model = new CreateIndexModel<Usuario>(indexKeys, indexOptions);
            _usuarios.Indexes.CreateOne(model);
        }

        // 📜 Lista todos os usuários com personagens
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
                    usuario = new
                    {
                        u.Id,
                        u.UsuarioNome,
                        u.Email
                    },
                    personagens
                });
            }

            return Ok(resultado);
        }

        // 🔍 Busca por ID
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(string id)
        {
            var usuario = await _usuarios.Find(u => u.Id == id).FirstOrDefaultAsync();
            if (usuario == null) return NotFound();

            var personagens = await _personagens.Find(p => p.IdUsuario == id).ToListAsync();

            return Ok(new
            {
                usuario = new
                {
                    usuario.Id,
                    usuario.UsuarioNome,
                    usuario.Email
                },
                personagens
            });
        }

        // ➕ Cria novo usuário
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateUsuarioDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Usuario) || string.IsNullOrWhiteSpace(dto.Senha))
                return BadRequest("Usuário e senha são obrigatórios.");

            // 🚫 Verifica duplicações
            var existente = await _usuarios
                .Find(u => u.UsuarioNome == dto.Usuario || u.Email == dto.Email)
                .FirstOrDefaultAsync();

            if (existente != null)
                return Conflict("Usuário ou e-mail já cadastrado.");

            var usuario = new Usuario
            {
                UsuarioNome = dto.Usuario,
                SenhaHash = Usuario.HashPassword(dto.Senha),
                Email = dto.Email
            };

            await _usuarios.InsertOneAsync(usuario);

            return CreatedAtAction(nameof(GetById), new { id = usuario.Id }, new
            {
                usuario.Id,
                usuario.UsuarioNome,
                usuario.Email
            });
        }


        // 🔑 Login
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] dynamic data)
        {
            string usuarioNome = data?.usuario ?? string.Empty;
            string senha = data?.senha ?? string.Empty;

            var usuario = await _usuarios.Find(u => u.UsuarioNome == usuarioNome).FirstOrDefaultAsync();
            if (usuario == null || !usuario.VerifyPassword(senha))
                return Unauthorized("Usuário ou senha inválidos.");

            return Ok(new
            {
                message = "Login bem-sucedido!",
                usuario.Id,
                usuario.UsuarioNome,
                usuario.Email
            });
        }

        // ✏️ Atualizar
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string id, [FromBody] Usuario usuario)
        {
            var result = await _usuarios.ReplaceOneAsync(u => u.Id == id, usuario);
            return result.MatchedCount == 0 ? NotFound() : Ok(usuario);
        }

        // ❌ Remover
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var result = await _usuarios.DeleteOneAsync(u => u.Id == id);
            return result.DeletedCount == 0 ? NotFound() : NoContent();
        }
    }
}
