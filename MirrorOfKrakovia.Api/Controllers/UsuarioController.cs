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
        public async Task<IActionResult> Login([FromBody] LoginUsuarioDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Usuario) || string.IsNullOrWhiteSpace(dto.Senha))
                return BadRequest("Usuário e senha são obrigatórios.");

            var usuario = await _usuarios.Find(u => u.UsuarioNome == dto.Usuario).FirstOrDefaultAsync();
            if (usuario == null || !usuario.VerifyPassword(dto.Senha))
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
        public async Task<IActionResult> Update(string id, [FromBody] UpdateUsuarioDto dto)
        {
            var usuario = await _usuarios.Find(u => u.Id == id).FirstOrDefaultAsync();
            if (usuario == null)
                return NotFound("Usuário não encontrado.");

            // 🚫 Verifica duplicações se alterou usuário/email
            if (!string.IsNullOrWhiteSpace(dto.Usuario) && dto.Usuario != usuario.UsuarioNome)
            {
                var jaExiste = await _usuarios.Find(u => u.UsuarioNome == dto.Usuario).FirstOrDefaultAsync();
                if (jaExiste != null)
                    return Conflict("Nome de usuário já está em uso.");
                usuario.UsuarioNome = dto.Usuario;
            }

            if (!string.IsNullOrWhiteSpace(dto.Email) && dto.Email != usuario.Email)
            {
                var jaExisteEmail = await _usuarios.Find(u => u.Email == dto.Email).FirstOrDefaultAsync();
                if (jaExisteEmail != null)
                    return Conflict("E-mail já está em uso.");
                usuario.Email = dto.Email;
            }

            // 🔒 Não alterar senha aqui!
            await _usuarios.ReplaceOneAsync(u => u.Id == id, usuario);

            return Ok(new
            {
                message = "Usuário atualizado com sucesso!",
                usuario.Id,
                usuario.UsuarioNome,
                usuario.Email
            });
        }


        [HttpPut("{id}/alterar-senha")]
        public async Task<IActionResult> AlterarSenha(string id, [FromBody] AlterarSenhaDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.SenhaAtual) || string.IsNullOrWhiteSpace(dto.NovaSenha))
                return BadRequest("Senha atual e nova senha são obrigatórias.");

            var usuario = await _usuarios.Find(u => u.Id == id).FirstOrDefaultAsync();
            if (usuario == null)
                return NotFound("Usuário não encontrado.");

            // 🔐 valida senha atual
            if (!usuario.VerifyPassword(dto.SenhaAtual))
                return Unauthorized("Senha atual incorreta.");

            // 🔒 atualiza senha
            usuario.SenhaHash = Usuario.HashPassword(dto.NovaSenha);
            await _usuarios.ReplaceOneAsync(u => u.Id == id, usuario);

            return Ok(new
            {
                message = "Senha alterada com sucesso!"
            });
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
