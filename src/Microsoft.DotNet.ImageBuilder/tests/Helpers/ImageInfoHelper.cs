﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using Microsoft.DotNet.ImageBuilder.Models.Image;

namespace Microsoft.DotNet.ImageBuilder.Tests.Helpers
{
    public static class ImageInfoHelper
    {
        public static PlatformData CreatePlatform(
            string dockerfile,
            string digest = null,
            string architecture = "amd64",
            string osType = "Linux",
            string osVersion = "focal",
            List<string> simpleTags = null,
            string baseImageDigest = null,
            DateTime? created = null,
            List<string> layers = null)
        {
            PlatformData platform = new()
            {
                Dockerfile = dockerfile,
                Digest = digest,
                Architecture = architecture,
                OsType = osType,
                OsVersion = osVersion,
                SimpleTags = simpleTags,
                BaseImageDigest = baseImageDigest,
            };

            if (created.HasValue)
            {
                platform.Created = created.Value;
            }

            if (layers != null)
            {
                platform.Layers = layers;
            }

            return platform;
        }
    }
}
