// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NuGetPackageVerifier.Rules
{
    public class AssemblyIsBuiltInReleaseConfigurationRule : IPackageVerifierRule
    {
        // TODO: Revert to using Mono.Cecil when https://github.com/jbevain/cecil/issues/306 is fixed.
        public IEnumerable<PackageVerifierIssue> Validate(PackageAnalysisContext context)
        {
            var issues = new List<PackageVerifierIssue>();
            foreach (var currentFile in context.PackageReader.GetFiles())
            {
                var extension = Path.GetExtension(currentFile);
                if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using (var packageFileStream = context.PackageReader.GetStream(currentFile))
                using (var stream = new MemoryStream())
                {
                    // packageFileStream is not a seekable stream. So wrap it in a memory stream for PEReader to consume.
                    packageFileStream.CopyTo(stream);
                    stream.Position = 0;
                    PEReader peReader = null;
                    MetadataReader reader;
                    try
                    {
                        peReader = new PEReader(stream);
                        reader = peReader.GetMetadataReader();
                    }
                    catch
                    {
                        // This assembly is not managed.
                        peReader?.Dispose();
                        continue;
                    }

                    using (peReader)
                    {
                        foreach (var handle in reader.CustomAttributes)
                        {
                            var customAttribute = reader.GetCustomAttribute(handle);
                            string typeName;
                            if (customAttribute.Constructor.Kind == HandleKind.MemberReference)
                            {
                                var ctor = reader.GetMemberReference((MemberReferenceHandle)customAttribute.Constructor);
                                var type = reader.GetTypeReference((TypeReferenceHandle)ctor.Parent);
                                typeName = reader.GetString(type.Name);
                            }
                            else if (customAttribute.Constructor.Kind == HandleKind.MethodDefinition)
                            {
                                var method = reader.GetMethodDefinition((MethodDefinitionHandle)customAttribute.Constructor);
                                var type = reader.GetTypeDefinition(method.GetDeclaringType());
                                typeName = reader.GetString(type.Name);
                            }
                            else
                            {
                                throw new InvalidOperationException($"Unknown CustomAttribute constructor type '{customAttribute.Constructor.Kind}");
                            }

                            if (string.Equals(typeof(DebuggableAttribute).Name, typeName, StringComparison.Ordinal))
                            {
                                var attribute = customAttribute.DecodeValue(NullProvider.Instance);

                                var debuggingMode = (DebuggableAttribute.DebuggingModes)attribute.FixedArguments.Single().Value;
                                if (debuggingMode.HasFlag(DebuggableAttribute.DebuggingModes.Default) ||
                                    debuggingMode.HasFlag(DebuggableAttribute.DebuggingModes.DisableOptimizations))
                                {
                                    issues.Add(PackageIssueFactory.AssemblyHasIncorrectBuildConfiguration(currentFile));
                                };

                                break;
                            }
                        }
                    }
                }
            }

            return issues;
        }

        private class NullProvider : ICustomAttributeTypeProvider<object>
        {
            public static NullProvider Instance { get; } = new NullProvider();

            public object GetPrimitiveType(PrimitiveTypeCode typeCode) => null;
            public object GetSystemType() => null;
            public object GetSZArrayType(object elementType) => null;
            public object GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => null;
            public object GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => null;
            public object GetTypeFromSerializedName(string name) => null;
            public PrimitiveTypeCode GetUnderlyingEnumType(object type) => PrimitiveTypeCode.Int32;
            public bool IsSystemType(object type) => false;
        }
    }
}
