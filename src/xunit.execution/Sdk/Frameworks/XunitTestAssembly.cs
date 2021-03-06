﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security;
using Xunit.Abstractions;
using Xunit.Serialization;

namespace Xunit.Sdk
{
    /// <summary>
    /// The implementation of <see cref="ITestAssembly"/> that is used by xUnit.net v2.
    /// </summary>
    [Serializable]
    [DebuggerDisplay(@"\{ assembly = {Assembly.AssemblyPath}, config = {ConfigFileName} \}")]
    public class XunitTestAssembly : LongLivedMarshalByRefObject, ITestAssembly, ISerializable, IGetTypeData
    {
        /// <summary/>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Called by the de-serializer", error: true)]
        public XunitTestAssembly() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="XunitTestAssembly"/> class.
        /// </summary>
        public XunitTestAssembly(IAssemblyInfo assembly, string configFileName)
        {
            ConfigFileName = configFileName;
            Assembly = assembly;
        }

        /// <inheritdoc/>
        public IAssemblyInfo Assembly { get; set; }

        /// <inheritdoc/>
        public string ConfigFileName { get; set; }

        // -------------------- Serialization --------------------

        /// <inheritdoc/>
        [SecurityCritical]
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("AssemblyPath", Assembly.AssemblyPath);
            info.AddValue("ConfigFileName", ConfigFileName);
        }

        /// <inheritdoc/>
        public void GetData(XunitSerializationInfo info)
        {
            info.AddValue("AssemblyPath", Assembly.AssemblyPath);
            info.AddValue("ConfigFileName", ConfigFileName);
        }

        /// <inheritdoc/>
        protected XunitTestAssembly(SerializationInfo info, StreamingContext context)
        {
            ConfigFileName = info.GetString("ConfigFileName");

            var assemblyPath = info.GetString("AssemblyPath");

#if !WINDOWS_PHONE_APP && !WIN8_STORE
            var assembly = AppDomain.CurrentDomain
                                    .GetAssemblies()
                                    .First(a => !a.IsDynamic && String.Equals(a.GetLocalCodeBase(), assemblyPath, StringComparison.OrdinalIgnoreCase));
#else
            // On WPA, this will be the assemblyname
            var assembly = System.Reflection.Assembly.Load(new AssemblyName
            {
                Name = Path.GetFileNameWithoutExtension(assemblyPath)
            });
#endif

            Assembly = Reflector.Wrap(assembly);
        }

        /// <inheritdoc/>
        public void SetData(XunitSerializationInfo info)
        {
            var assemblyPath = info.GetString("AssemblyPath");
            var assembly = System.Reflection.Assembly.Load(new AssemblyName
            {
                Name = Path.GetFileNameWithoutExtension(assemblyPath)
            });

            ConfigFileName = info.GetString("ConfigFileName");
            Assembly = Reflector.Wrap(assembly);
        }
    }
}