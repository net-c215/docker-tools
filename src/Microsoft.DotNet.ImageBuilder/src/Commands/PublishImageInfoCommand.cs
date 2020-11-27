﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.DotNet.ImageBuilder.Models.Image;
using Microsoft.DotNet.ImageBuilder.Services;
using Microsoft.DotNet.ImageBuilder.ViewModel;
using Microsoft.DotNet.VersionTools.Automation.GitHubApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;

#nullable enable
namespace Microsoft.DotNet.ImageBuilder.Commands
{
    [Export(typeof(ICommand))]
    public class PublishImageInfoCommand : ManifestCommand<PublishImageInfoOptions>
    {
        private readonly IGitHubClientFactory _gitHubClientFactory;
        private readonly ILoggerService _loggerService;
        private readonly IAzdoGitHttpClientFactory _azdoGitHttpClientFactory;
        private readonly HttpClient _httpClient;
        private const string CommitMessage = "Merging Docker image info updates from build";

        [ImportingConstructor]
        public PublishImageInfoCommand(IGitHubClientFactory gitHubClientFactory, ILoggerService loggerService, IHttpClientProvider httpClientProvider,
            IAzdoGitHttpClientFactory azdoGitHttpClientFactory)
        {
            if (httpClientProvider is null)
            {
                throw new ArgumentNullException(nameof(httpClientProvider));
            }

            _gitHubClientFactory = gitHubClientFactory ?? throw new ArgumentNullException(nameof(gitHubClientFactory));
            _loggerService = loggerService ?? throw new ArgumentNullException(nameof(loggerService));
            _azdoGitHttpClientFactory = azdoGitHttpClientFactory ?? throw new ArgumentNullException(nameof(azdoGitHttpClientFactory));

            _httpClient = httpClientProvider.GetClient();
        }

        public override async Task ExecuteAsync()
        {
            Uri imageInfoPathIdentifier = GitHelper.GetBlobUrl(Options.GitOptions);

            string? imageInfoContent = await GetUpdatedImageInfoAsync();

            if (imageInfoContent is null)
            {
                _loggerService.WriteMessage($"No changes to the '{imageInfoPathIdentifier}' file were needed.");
                return;
            }

            _loggerService.WriteMessage(
                $"The '{imageInfoPathIdentifier}' file has been updated with the following content:" +
                    Environment.NewLine + imageInfoContent + Environment.NewLine);

            if (!Options.IsDryRun)
            {
                await UpdateGitHubAsync(imageInfoContent, imageInfoPathIdentifier);
                //await UpdateAzdoAsync(imageInfoContent, imageInfoPathIdentifier);
            }
        }

        private async Task UpdateAzdoAsync(string imageInfoContent, Uri imageInfoPathIdentifier)
        {
            (Uri baseUrl, VssCredentials credentials) = Options.AzdoOptions.GetConnectionDetails();

            using IAzdoGitHttpClient gitHttpClient = _azdoGitHttpClientFactory.GetClient(baseUrl, credentials);

            GitRepository repo = (await gitHttpClient.GetRepositoriesAsync())
                .First(repo => repo.Name == Options.AzdoOptions.Repo);
            GitRef branchRef = (await gitHttpClient.GetBranchRefsAsync(repo.Id))
                .First(branch => branch.Name == $"refs/heads/{Options.AzdoOptions.Branch}");

            GitPush push = await gitHttpClient.PushChangesAsync(CommitMessage, repo.Id, branchRef, new Dictionary<string, string>
            {
                { Options.AzdoOptions.Path, imageInfoContent }
            });

            TeamFoundation.SourceControl.WebApi.GitCommit commit =
                await gitHttpClient.GetCommitAsync(push.Commits.First().CommitId, repo.Id);

            _loggerService.WriteMessage($"The '{imageInfoPathIdentifier}' file was updated ({commit.RemoteUrl}).");
        }

        private async Task UpdateGitHubAsync(string imageInfoContent, Uri imageInfoPathIdentifier)
        {
            IGitHubClient gitHubClient = _gitHubClientFactory.GetClient(Options.GitOptions.ToGitHubAuth(), Options.IsDryRun);
            await GitHelper.ExecuteGitOperationsWithRetryAsync(async () =>
            {
                VersionTools.Automation.GitHubApi.GitObject imageInfoGitObject = new VersionTools.Automation.GitHubApi.GitObject
                {
                    Path = Options.GitOptions.Path,
                    Type = VersionTools.Automation.GitHubApi.GitObject.TypeBlob,
                    Mode = VersionTools.Automation.GitHubApi.GitObject.ModeFile,
                    Content = imageInfoContent
                };

                GitReference gitRef = await GitHelper.PushChangesAsync(
                    gitHubClient, Options, CommitMessage,
                    branch => Task.FromResult<IEnumerable<VersionTools.Automation.GitHubApi.GitObject>>(
                        new VersionTools.Automation.GitHubApi.GitObject[] { imageInfoGitObject }));

                Uri commitUrl = GitHelper.GetCommitUrl(Options.GitOptions, gitRef.Object.Sha);
                _loggerService.WriteMessage($"The '{imageInfoPathIdentifier}' file was updated ({commitUrl}).");
            });
        }

        private async Task<string?> GetUpdatedImageInfoAsync()
        {
            ImageArtifactDetails srcImageArtifactDetails = ImageInfoHelper.LoadFromFile(Options.ImageInfoPath, Manifest);

            string repoPath = await GitHelper.DownloadAndExtractGitRepoArchiveAsync(_httpClient, Options.GitOptions);
            try
            {
                string repoImageInfoPath = Path.Combine(repoPath, Options.GitOptions.Path);
                string originalTargetImageInfoContents = File.ReadAllText(repoImageInfoPath);

                ImageArtifactDetails newImageArtifactDetails;

                if (originalTargetImageInfoContents != null)
                {
                    ImageArtifactDetails targetImageArtifactDetails = ImageInfoHelper.LoadFromContent(
                        originalTargetImageInfoContents, Manifest, skipManifestValidation: true);

                    RemoveOutOfDateContent(targetImageArtifactDetails);

                    ImageInfoMergeOptions options = new ImageInfoMergeOptions
                    {
                        ReplaceTags = true
                    };

                    ImageInfoHelper.MergeImageArtifactDetails(srcImageArtifactDetails, targetImageArtifactDetails, options);

                    newImageArtifactDetails = targetImageArtifactDetails;
                }
                else
                {
                    // If there is no existing file to update, there's nothing to merge with so the source data
                    // becomes the target data.
                    newImageArtifactDetails = srcImageArtifactDetails;
                }

                string newTargetImageInfoContents =
                    JsonHelper.SerializeObject(newImageArtifactDetails) + Environment.NewLine;

                if (originalTargetImageInfoContents != newTargetImageInfoContents)
                {
                    return newTargetImageInfoContents;
                }
                else
                {
                    return null;
                }
            }
            finally
            {
                Directory.Delete(repoPath, recursive: true);
            }
        }

        private void RemoveOutOfDateContent(ImageArtifactDetails imageArtifactDetails)
        {
            for (int repoIndex = imageArtifactDetails.Repos.Count - 1; repoIndex >= 0; repoIndex--)
            {
                RepoData repoData = imageArtifactDetails.Repos[repoIndex];

                // Since the registry name is not represented in the image info, make sure to compare the repo name with the
                // manifest's repo model name which isn't registry-qualified.
                RepoInfo manifestRepo = Manifest.AllRepos.FirstOrDefault(manifestRepo => manifestRepo.Name == repoData.Repo);

                // If there doesn't exist a matching repo in the manifest, remove it from the image info
                if (manifestRepo is null)
                {
                    imageArtifactDetails.Repos.Remove(repoData);
                    continue;
                }

                for (int imageIndex = repoData.Images.Count - 1; imageIndex >= 0; imageIndex--)
                {
                    ImageData imageData = repoData.Images[imageIndex];
                    ImageInfo manifestImage = imageData.ManifestImage;

                    // If there doesn't exist a matching image in the manifest, remove it from the image info
                    if (manifestImage is null)
                    {
                        repoData.Images.Remove(imageData);
                        continue;
                    }

                    for (int platformIndex = imageData.Platforms.Count - 1; platformIndex >= 0; platformIndex--)
                    {
                        PlatformData platformData = imageData.Platforms[platformIndex];
                        PlatformInfo manifestPlatform = manifestImage.AllPlatforms
                            .FirstOrDefault(manifestPlatform => platformData.PlatformInfo == manifestPlatform);

                        // If there doesn't exist a matching platform in the manifest, remove it from the image info
                        if (manifestPlatform is null)
                        {
                            imageData.Platforms.Remove(platformData);
                        }
                    }
                }
            }

            if (imageArtifactDetails.Repos.Count == 0)
            {
                // Failsafe to prevent wiping out the image info due to a bug in the logic
                throw new InvalidOperationException(
                    "Removal of out-of-date content resulted in there being no content remaining in the target image info file. Something is probably wrong with the logic.");
            }
        }
    }
}
#nullable disable
