using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FinTube.Services;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTube.Api;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("fintube")]
[Produces(MediaTypeNames.Application.Json)]
public class FinTubeActivityController : ControllerBase
{
        private readonly ILogger<FinTubeActivityController> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly FinTubeDownloadQueue _queue;
        private readonly FinTubeDependencyManager _deps;

        public FinTubeActivityController(
            ILoggerFactory loggerFactory,
            ILibraryManager libraryManager,
            FinTubeDownloadQueue queue,
            FinTubeDependencyManager deps)
        {
            _logger = loggerFactory.CreateLogger<FinTubeActivityController>();
            _libraryManager = libraryManager;
            _queue = queue;
            _deps = deps;
        }

        [HttpPost("submit_dl")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<Dictionary<string, object>> FinTubeDownload([FromBody] FinTubeData data)
        {
            try
            {
                _logger.LogInformation("FinTubeDownload queued : {ytid} to {targetfolder}, prefer free format: {preferfreeformat} audio only: {audioonly}", data.ytid, data.targetfolder, data.preferfreeformat, data.audioonly);

                if (string.IsNullOrWhiteSpace(data.ytid))
                    throw new Exception("No video id provided");

                var job = _queue.Enqueue(data);

                return Ok(new Dictionary<string, object>
                {
                    { "message", "Download queued" },
                    { "jobId", job.Id }
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, e.Message);
                return StatusCode(500, new Dictionary<string, object>() { { "message", e.Message } });
            }
        }

        [HttpGet("jobs")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<Dictionary<string, object>> FinTubeJobs()
        {
            var jobs = _queue.GetJobs().Select(j => new Dictionary<string, object?>
            {
                { "id", j.Id },
                { "label", j.Label },
                { "ytid", j.Data.ytid },
                { "status", j.Status.ToString() },
                { "progress", Math.Round(j.Progress, 1) },
                { "log", j.Log },
                { "error", j.Error },
                { "createdAt", j.CreatedAt },
                { "startedAt", j.StartedAt },
                { "finishedAt", j.FinishedAt }
            }).ToArray();

            return Ok(new Dictionary<string, object> { { "data", jobs } });
        }

        [HttpPost("jobs/clear")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult FinTubeClearJobs()
        {
            _queue.ClearFinished();
            return Ok();
        }

        [HttpPost("jobs/{id}/retry")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<Dictionary<string, object>> FinTubeRetryJob(string id)
        {
            var job = _queue.Retry(id);
            if (job is null)
                return NotFound(new Dictionary<string, object> { { "message", "Unknown job" } });

            return Ok(new Dictionary<string, object>
            {
                { "message", "Download re-queued" },
                { "jobId", job.Id }
            });
        }

        [HttpGet("libraries")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<Dictionary<string, object>> FinTubeLibraries()
        {
            try
            {
                _logger.LogInformation("FinTubeDLibraries count: {count}", _libraryManager.GetVirtualFolders().Count);

                Dictionary<string, object> response = new Dictionary<string, object>();
                response.Add("data", _libraryManager.GetVirtualFolders().Select(i => i.Locations).ToArray());
                return Ok(response);
            }
            catch(Exception e)
            {
                _logger.LogError(e, e.Message);
                return StatusCode(500, new Dictionary<string, object>() {{"message", e.Message}});
            }
        }

        [HttpGet("dependencies")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<Dictionary<string, object>> FinTubeDependencies()
        {
            return Ok(new Dictionary<string, object>
            {
                { "binDir", _deps.BinDir },
                { "ytdlp", _deps.GetYtdlpStatus() },
                { "deno", _deps.GetDenoStatus() },
                { "progress", new Dictionary<string, object>
                    {
                        { "ytdlp", _deps.GetProgress("ytdlp") },
                        { "deno", _deps.GetProgress("deno") }
                    }
                }
            });
        }

        [HttpPost("dependencies/{name}/install")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<Dictionary<string, object>> FinTubeInstallDependency(string name)
        {
            try
            {
                // Returns immediately; the frontend polls GET dependencies for progress.
                var progress = _deps.StartInstall(name);
                return Ok(new Dictionary<string, object> { { "progress", progress } });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to start install of dependency {Name}", name);
                return StatusCode(500, new Dictionary<string, object> { { "message", e.Message } });
            }
        }
}
