using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Penguin.Cms.Files;
using Penguin.Cms.Files.Repositories;
using Penguin.Extensions.Strings;
using Penguin.Files.Services;
using Penguin.Messaging.Abstractions.Interfaces;
using Penguin.Messaging.Persistence.Messages;
using Penguin.Web.Abstractions.Interfaces;
using Penguin.Web.Data;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace Penguin.Web.Errors.Middleware
{
    //https://exceptionnotfound.net/using-middleware-to-log-requests-and-responses-in-asp-net-core/
    public class DatabaseFileServer : IPenguinMiddleware, IMessageHandler
    {
        public static ConcurrentDictionary<string, bool> ExistingFiles;

        private readonly RequestDelegate _next;

        //TODO: Learn what this is
        public DatabaseFileServer(RequestDelegate next)
        {
            this._next = next;
        }

        public static void AcceptMessage(Penguin.Messaging.Application.Messages.Startup message)
        {
            ExistingFiles = new ConcurrentDictionary<string, bool>();
        }

        public static void AcceptMessage(Updating<DatabaseFile> message)
        {
            if (ExistingFiles.ContainsKey(message.Target.FilePath))
            {
                ExistingFiles[message.Target.FilePath] = !message.Target.IsDeleted;
            }
            else
            {
                ExistingFiles.TryAdd(message.Target.FilePath, !message.Target.IsDeleted);
            }
        }

        public async Task Invoke(HttpContext context)
        {
            string requestPath = context.Request.Path.Value.Split('?')[0];

            string root = context.RequestServices.GetService<FileService>()?.GetUserFilesRoot() ?? Path.Combine(Directory.GetCurrentDirectory(), "Files");

            string filePath = Path.Combine(root, requestPath.Trim('/')).Replace("/", "\\");

            DatabaseFileRepository databaseFileRepository = context.RequestServices.GetService<DatabaseFileRepository>();

            bool Exists = true;
            DatabaseFile found = null;

            if (ExistingFiles != null && !ExistingFiles.TryGetValue(filePath, out Exists))
            {
                found = databaseFileRepository.GetByFullName(filePath);

                Exists = found != null && !found.IsDeleted;

                ExistingFiles.TryAdd(filePath, Exists);
            }

            if (Exists && found is null)
            {
                found = databaseFileRepository.GetByFullName(filePath);
            }

            if (found != null)
            {
                await ReturnFile(context, found);
                return;
            }
            else
            {
                await this._next(context).ConfigureAwait(false);
            }
        }

        private static async Task ReturnFile(HttpContext context, DatabaseFile databaseFile)
        {
            string Extension = databaseFile.FileName.FromLast(".");
            string MimeType = MimeMappings.GetMimeType(Extension);

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = MimeType;

            byte[] fileData;

            if (databaseFile.Data.Length != 0)
            {
                fileData = databaseFile.Data;
            }
            else
            {
                fileData = File.ReadAllBytes(databaseFile.FullName);
            }

            context.Response.ContentLength = fileData.Length;

            using (Stream stream = context.Response.Body)
            {
                await stream.WriteAsync(fileData, 0, fileData.Length);
                await stream.FlushAsync();
            }
        }
    }
}