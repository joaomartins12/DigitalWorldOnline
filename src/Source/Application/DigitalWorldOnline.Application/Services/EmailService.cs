using DigitalWorldOnline.Commons.Interfaces;
using System.Net.Mail;
using System.Net;

namespace DigitalWorldOnline.Application.Services
{
    public class EmailService : IEmailService
    {
        public EmailService() { }

        public void Send(string destination)
        {
            // Configurações do servidor de email
            string smtpServer = "digitaluniversedn@gmail.com\n"; // Substitua pelo servidor SMTP real
            int smtpPort = 587; // Porta SMTP padrão 587 /465 for SSL
            string smtpUsername = "digitaluniversedn@gmail.com\n"; // Seu endereço de email
            string smtpPassword = "siberia90"; // Sua senha de email

            // Criando a mensagem de email
            MailMessage message = new MailMessage();
            message.From = new MailAddress(smtpUsername);
            message.To.Add("digitaluniversedn@gmail.com\n"); // Endereço do destinatário
            message.Subject = "Digital Universe Online - Account created.";
            message.Body = "Account created! Set your password here: http://digitaluniverse.com:2052/.";

            // Configurando o cliente SMTP
            SmtpClient smtpClient = new SmtpClient(smtpServer, smtpPort);
            smtpClient.Credentials = new NetworkCredential(smtpUsername, smtpPassword);
            smtpClient.EnableSsl = true;

            try
            {
                // Enviando o email
                smtpClient.Send(message);
                Console.WriteLine("Email enviado com sucesso!");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Ocorreu um erro ao enviar o email: " + ex.Message);
            }
        }
    }
}
