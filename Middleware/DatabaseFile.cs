using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Penguin.Cms.Files;
using Penguin.Cms.Files.Repositories;
using Penguin.Extensions.String;
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

namespace Penguin.Cms.Web.Files.Middleware
{
    //https://exceptionnotfound.net/using-middleware-to-log-requests-and-responses-in-asp-net-core/
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1305:Specify IFormatProvider", Justification = "<Pending>")]
    [Obsolete]
    public class DatabaseFileServer : IPenguinMiddleware, IMessageHandler
    {
        private const long CHUNK_SIZE = 1_000_000;
        private static ConcurrentDictionary<string, bool> ExistingFiles;

        private readonly RequestDelegate _next;

        //TODO: Learn what this is
        public DatabaseFileServer(RequestDelegate next)
        {
            _next = next;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "<Pending>")]
        public static void AcceptMessage(Messaging.Application.Messages.Startup message)
        {
            ExistingFiles = new ConcurrentDictionary<string, bool>();
        }

        public static void AcceptMessage(Updating<DatabaseFile> message)
        {
            if (message is null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (ExistingFiles.ContainsKey(message.Target.FilePath))
            {
                ExistingFiles[message.Target.FilePath] = !message.Target.IsDeleted;
            }
            else
            {
                _ = ExistingFiles.TryAdd(message.Target.FilePath, !message.Target.IsDeleted);
            }
        }

        public async Task Invoke(HttpContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            string requestPath = context.Request.Path.Value.Split('?')[0];

            string root = context.RequestServices.GetService<FileService>()?.GetUserFilesRoot() ?? Path.Combine(Directory.GetCurrentDirectory(), "Files");

            string filePath = Path.Combine(root, requestPath.Trim('/')).Replace("/", "\\");

            DatabaseFileRepository databaseFileRepository = context.RequestServices.GetService<DatabaseFileRepository>();

            bool Exists = true;
            DatabaseFile found = null;

            if (found is null && ExistingFiles != null && !ExistingFiles.TryGetValue(filePath, out Exists))
            {
                found = databaseFileRepository.GetByFullName(filePath);

                Exists = found != null && !found.IsDeleted;

                _ = ExistingFiles.TryAdd(filePath, Exists);
            }

            if (Exists && found is null)
            {
                found = databaseFileRepository.GetByFullName(filePath);
            }

            if (found != null)
            {
                await ReturnFile(context, found).ConfigureAwait(false);
                return;
            }
            else
            {
                await _next(context).ConfigureAwait(false);
            }
        }

        private static async Task RangeDownload(string fullpath, HttpContext context)
        {
            long size, start, end, length;
            using (StreamReader reader = new(fullpath))
            {
                size = reader.BaseStream.Length;
                start = 0;
                end = Math.Min(size - 1, CHUNK_SIZE);
                length = end - start + 1;

                // Now that we've gotten so far without errors we send the accept range header
                /* At the moment we only support single ranges.
                 * Multiple ranges requires some more work to ensure it works correctly
                 * and comply with the spesifications: http://www.w3.org/Protocols/rfc2616/rfc2616-sec19.html#sec19.2
                 *
                 * Multirange support annouces itself with:
                 * header('Accept-Ranges: bytes');
                 *
                 * Multirange content must be sent with multipart/byteranges mediatype,
                 * (mediatype = mimetype)
                 * as well as a boundry header to indicate the various chunks of data.
                 */
                context.Response.Headers.Add("Accept-Ranges", "0-" + size);
                // header('Accept-Ranges: bytes');
                // multipart/byteranges
                // http://www.w3.org/Protocols/rfc2616/rfc2616-sec19.html#sec19.2

                if (!string.IsNullOrEmpty(context.Request.Headers["Range"]))
                {
                    long anotherStart = start;
                    long anotherEnd = end;
                    string[] arr_split = context.Request.Headers["Range"].ToString().Split('=');
                    string range = arr_split[1];

                    // Make sure the client hasn't sent us a multibyte range
                    if (range.IndexOf(",") > -1)
                    {
                        // (?) Shoud this be issued here, or should the first
                        // range be used? Or should the header be ignored and
                        // we output the whole content?
                        context.Response.Headers.Add("Content-Range", "bytes " + start + "-" + end + "/" + size);
                        context.Response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                        return;
                    }

                    // If the range starts with an '-' we start from the beginning
                    // If not, we forward the file pointer
                    // And make sure to get the end byte if spesified
                    if (range.StartsWith("-"))
                    {
                        // The n-number of the last bytes is requested
                        anotherStart = size - Convert.ToInt64(range[1..]);
                    }
                    else
                    {
                        arr_split = range.Split('-');
                        anotherStart = Convert.ToInt64(arr_split[0]);

                        anotherEnd = !string.IsNullOrWhiteSpace(arr_split[1])
                            ? arr_split.Length > 1 && long.TryParse(arr_split[1], out long temp) ? Convert.ToInt64(arr_split[1]) : end
                            : Math.Min(anotherStart + CHUNK_SIZE, size);
                    }
                    /* Check the range and make sure it's treated according to the specs.
                     * http://www.w3.org/Protocols/rfc2616/rfc2616-sec14.html
                     */
                    // End bytes can not be larger than $end.
                    anotherEnd = anotherEnd > size - 1 ? size - 1 : anotherEnd;
                    // Validate the requested range and return an error if it's not correct.
                    if (anotherStart > anotherEnd || anotherStart > size - 1 || anotherEnd >= size)
                    {
                        context.Response.Headers.Add("Content-Range", "bytes " + start + "-" + end + "/" + size);
                        context.Response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                        return;
                    }
                    start = anotherStart;
                    end = anotherEnd;

                    length = end - start + 1; // Calculate new content length
                    long fp = reader.BaseStream.Seek(start, SeekOrigin.Begin);
                    context.Response.StatusCode = 206;
                }
            }
            // Notify the client the byte range we'll be outputting
            context.Response.Headers.Add("Content-Range", "bytes " + start + "-" + end + "/" + size);
            context.Response.Headers.Add("Content-Length", length.ToString());

            byte[] response = new byte[length];
            using (BinaryReader reader = new(new FileStream(fullpath, FileMode.Open)))
            {
                _ = reader.BaseStream.Seek(start, SeekOrigin.Begin);
                _ = reader.Read(response, 0, (int)length);
            }

            // Start buffered download
            await context.Response.Body.WriteAsync(response).ConfigureAwait(false);
            await context.Response.Body.FlushAsync().ConfigureAwait(false);
        }

        private static async Task ReturnFile(HttpContext context, DatabaseFile databaseFile)
        {
            string Extension = databaseFile.FileName.FromLast(".");
            string MimeType = MimeMappings.GetMimeType(Extension);

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = MimeType;

            if (string.IsNullOrEmpty(context.Request.Headers["Range"]))
            {
                byte[] fileData = databaseFile.Data.Length != 0 ? databaseFile.Data : File.ReadAllBytes(databaseFile.FullName);
                context.Response.ContentLength = fileData.Length;

                using Stream stream = context.Response.Body;
                await stream.WriteAsync(fileData).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            else
            {
                await RangeDownload(databaseFile.FullName, context).ConfigureAwait(false);
            }
        }
    }
}