namespace MirrorOfKrakovia.Api.Models.Dtos
{
    public class CreateUsuarioDto
    {
        public string Usuario { get; set; } = string.Empty;
        public string Senha { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }
}
