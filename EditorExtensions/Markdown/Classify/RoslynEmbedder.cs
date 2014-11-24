﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Html.Editor.Projection;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Microsoft.Web.Editor;
using Microsoft.Web.Editor.EditorHelpers;

namespace MadsKristensen.EditorExtensions.Markdown.Classify
{
    public abstract class RoslynEmbedder : ICodeLanguageEmbedder
    {
        public IReadOnlyCollection<string> GetBlockWrapper(IEnumerable<string> code) { return new string[0]; }

        static readonly string referenceAssemblyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5"
        );
        static readonly IReadOnlyCollection<string> DefaultReferences = new[] {
            "mscorlib",
            "System",
            "System.Core",
            "System.Data",
            "System.Net.Http",
            "System.Net.Http.WebRequest",
            "System.Xml.Linq",
            "System.Web",
            "System.Windows.Forms",
            "WindowsBase",
            "PresentationCore",
            "PresentationFramework",
        };

        // Copied from VSEmbed.Roslyn.EditorWorkspace
        // This contains all of the ugly hacks needed
        // to make the Roslyn editor fully functional
        // on a custom Workspace
        class MarkdownWorkspace : Workspace
        {
            static readonly Type IWorkCoordinatorRegistrationService = Type.GetType("Microsoft.CodeAnalysis.SolutionCrawler.IWorkCoordinatorRegistrationService, Microsoft.CodeAnalysis.Features");

            readonly Dictionary<DocumentId, ITextBuffer> documentBuffers = new Dictionary<DocumentId, ITextBuffer>();
            public MarkdownWorkspace(HostServices host) : base(host, WorkspaceKind.MiscellaneousFiles)
            {
                var wcrService = typeof(HostWorkspaceServices)
                    .GetMethod(nameof(HostWorkspaceServices.GetService))
                    .MakeGenericMethod(IWorkCoordinatorRegistrationService)
                    .Invoke(Services, null);

                IWorkCoordinatorRegistrationService.GetMethod("Register").Invoke(wcrService, new[] { this });
            }
            public Project AddProject(string name, string language)
            {
                ProjectInfo projectInfo = ProjectInfo.Create(ProjectId.CreateNewId(null), VersionStamp.Create(), name, name, language);
                OnProjectAdded(projectInfo);
                return CurrentSolution.GetProject(projectInfo.Id);
            }

            ///<summary>Creates a new document linked to an existing text buffer.</summary>
            public Document CreateDocument(ProjectId projectId, ITextBuffer buffer)
            {
                var id = DocumentId.CreateNewId(projectId);
                documentBuffers.Add(id, buffer);

                // Our GetFileName() extension (which should probably be deleted) doesn't work on projection buffers
                var docInfo = DocumentInfo.Create(id, TextBufferExtensions.GetFileName(buffer) ?? "Markdown Embedded Code",
                    loader: TextLoader.From(buffer.AsTextContainer(), VersionStamp.Create()),
                    sourceCodeKind: SourceCodeKind.Script
                );
                OnDocumentAdded(docInfo);
                OnDocumentOpened(id, buffer.AsTextContainer());
                buffer.Changed += delegate { OnDocumentContextUpdated(id); };
                return CurrentSolution.GetDocument(id);
            }

            protected override void AddMetadataReference(ProjectId projectId, MetadataReference metadataReference)
            {
                OnMetadataReferenceAdded(projectId, metadataReference);
            }
            protected override void ChangedDocumentText(DocumentId id, SourceText text)
            {
                OnDocumentTextChanged(id, text, PreservationMode.PreserveValue);
                UpdateText(text, documentBuffers[id], EditOptions.DefaultMinimalChange);
            }

            // Stolen from Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem.DocumentProvider.StandardTextDocument
            private static void UpdateText(SourceText newText, ITextBuffer buffer, EditOptions options)
            {
                using (ITextEdit textEdit = buffer.CreateEdit(options, null, null))
                {
                    SourceText oldText = buffer.CurrentSnapshot.AsText();
                    foreach (var current in newText.GetTextChanges(oldText))
                    {
                        textEdit.Replace(current.Span.Start, current.Span.Length, current.NewText);
                    }
                    textEdit.Apply();
                }
            }

            public override bool CanApplyChange(ApplyChangesKind feature)
            {
                switch (feature)
                {
                    case ApplyChangesKind.AddMetadataReference:
                    case ApplyChangesKind.RemoveMetadataReference:
                    case ApplyChangesKind.ChangeDocument:
                        return true;
                    case ApplyChangesKind.AddProject:
                    case ApplyChangesKind.RemoveProject:
                    case ApplyChangesKind.AddProjectReference:
                    case ApplyChangesKind.RemoveProjectReference:
                    case ApplyChangesKind.AddDocument:
                    case ApplyChangesKind.RemoveDocument:
                    default:
                        return false;
                }
            }
        }

        static readonly Dictionary<string, string> contentTypeLanguages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "CSharp", LanguageNames.CSharp },
            { "Basic", LanguageNames.VisualBasic }
        };
        [Import]
        public SVsServiceProvider ServiceProvider { get; set; }
        [Import]
        public IVsEditorAdaptersFactoryService EditorAdaptersFactory { get; set; }
        [Import]
        public VisualStudioWorkspace VSWorkspace { get; set; }
        public void OnBlockCreated(ITextBuffer editorBuffer, LanguageProjectionBuffer projectionBuffer)
        {
            var componentModel = (IComponentModel)ServiceProvider.GetService(typeof(SComponentModel));

            var workspace = editorBuffer.Properties.GetOrCreateSingletonProperty(() =>
                new MarkdownWorkspace(MefV1HostServices.Create(componentModel.DefaultExportProvider))
            );

            var contentType = projectionBuffer.IProjectionBuffer.ContentType.DisplayName;
            var project = editorBuffer.Properties.GetOrCreateSingletonProperty(contentType, () =>
            {
                var newProject = workspace.AddProject(
                    "Sample " + contentType + " Project",
                    contentTypeLanguages[contentType]
                );
                workspace.TryApplyChanges(workspace.CurrentSolution.AddMetadataReferences(
                    newProject.Id,
                    DefaultReferences.Select(name => VSWorkspace.CreatePortableExecutableReference(
                        Path.Combine(referenceAssemblyPath, name + ".dll"),
                        MetadataReferenceProperties.Assembly
                    ))
                ));
                return newProject;
            });
            workspace.CreateDocument(project.Id, projectionBuffer.IProjectionBuffer);

            WindowHelpers.WaitFor(delegate
            {
                var textView = TextViewConnectionListener.GetFirstViewForBuffer(editorBuffer);
                if (textView == null) return false;
                InstallCommandTarget(textView, projectionBuffer.IProjectionBuffer);
                return true;
            });
        }

        #region OleCommandTarget Hackery
        // This horror is necessary to forward IOleCommandTarget commands to Roslyn's
        // internal commanding system.  See https://roslyn.codeplex.com/workitem/243.

        private void InstallCommandTarget(ITextView textView, ITextBuffer subjectBuffer)
        {
            var roslynCommandFilter = CreateCommandTarget(textView, subjectBuffer);
            // The VenusCommandFilter ctor accepts a nextCommandTarget immediately;
            // this apparently comes from the bowels of COM interop code. I pass it
            // a meaningless value, then call the base class' Attach method to make
            // it register as a CommandFilter (and correctly set NCT).  You have no
            // hope of comprehending this code without carefully decompiling Roslyn
            roslynCommandFilter.GetType()
                .GetMethod("AttachToVsTextView", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(roslynCommandFilter, null);
        }

        static Dictionary<string, string> contentTypeToNamespace = new Dictionary<string, string> {
            { "CSharp", "CSharp" },
            { "Basic",  "VisualBasic" }
        };
        IOleCommandTarget CreateCommandTarget(ITextView textView, ITextBuffer subjectBuffer)
        {
            var ns = contentTypeToNamespace[subjectBuffer.ContentType.TypeName];
            // VisualBasicLanguageService & Package are in a different namespace than C#'s.
            var packageType = Type.GetType(("Microsoft.VisualStudio.LanguageServices.\{ns}.LanguageService.\{ns}Package, "
                                         + "Microsoft.VisualStudio.LanguageServices.\{ns}")
                                            .Replace("LanguageService.VisualBasicPackage", "VisualBasicPackage"));
            var languageServiceType = Type.GetType(("Microsoft.VisualStudio.LanguageServices.\{ns}.LanguageService.\{ns}LanguageService, "
                                                  + "Microsoft.VisualStudio.LanguageServices.\{ns}")
                                            .Replace("LanguageService.VisualBasicLanguageService", "VisualBasicLanguageService"));
            var projectShimType = Type.GetType("Microsoft.VisualStudio.LanguageServices.\{ns}.ProjectSystemShim.\{ns}Project, Microsoft.VisualStudio.LanguageServices.\{ns}");
            var oleCommandTargetType = Type.GetType("Microsoft.VisualStudio.LanguageServices.Implementation.Venus.VenusCommandFilter`3, "
                                                  + "Microsoft.VisualStudio.LanguageServices")
                .MakeGenericType(packageType, languageServiceType, projectShimType);


            // This returns a COM wrapper object which I cannot unwrap.  However,
            // calling it primes the AbstractPackage.languageService field, which
            // I can then grab.
            ServiceProvider.GetService(languageServiceType);
            var shell = (IVsShell)ServiceProvider.GetService(typeof(SVsShell));
            IVsPackage package;
            Guid packageGuid = packageType.GUID;
            shell.LoadPackage(ref packageGuid, out package);
            var languageService = Type.GetType("Microsoft.VisualStudio.LanguageServices.Implementation.LanguageService.AbstractPackage`2, "
                                             + "Microsoft.VisualStudio.LanguageServices").MakeGenericType(packageType, languageServiceType)
                .GetField("languageService", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(package);

            var mef = (IComponentModel)ServiceProvider.GetService(typeof(SComponentModel));
            return (IOleCommandTarget)Activator.CreateInstance(oleCommandTargetType,
                languageService,
                textView,
                mef.DefaultExportProvider.GetExport<object>("Microsoft.CodeAnalysis.Editor.ICommandHandlerServiceFactory").Value,           // commandHandlerServiceFactory
                subjectBuffer,
                EditorAdaptersFactory.GetViewAdapter(textView),   // nextCommandTarget; not used immediately (see our callsite)
                EditorAdaptersFactory
            );
        }
        #endregion

        public virtual string GlobalPrefix { get { return ""; } }
        public virtual string GlobalSuffix { get { return ""; } }
    }

    [Export(typeof(ICodeLanguageEmbedder))]
    [ContentType("CSharp")]
    public class CSharpEmbedder : RoslynEmbedder
    {
        public override string GlobalPrefix
        {
            get
            {
                return @"using System;
                         using System.Collections.Generic;
                         using System.Data;
                         using System.IO;
                         using System.Linq;
                         using System.Net;
                         using System.Net.Http;
                         using System.Net.Http.Formatting;
                         using System.Reflection;
                         using System.Text;
                         using System.Threading;
                         using System.Threading.Tasks;
                         using System.Xml;
                         using System.Xml.Linq;";
            }
        }
    }

    [Export(typeof(ICodeLanguageEmbedder))]
    [ContentType("Basic")]
    public class VBEmbedder : RoslynEmbedder
    {
        public override string GlobalPrefix
        {
            get
            {
                return @"Imports System
                        Imports System.Collections.Generic
                        Imports System.Data
                        Imports System.IO
                        Imports System.Linq
                        Imports System.Net
                        Imports System.Net.Http
                        Imports System.Net.Http.Formatting
                        Imports System.Reflection
                        Imports System.Text
                        Imports System.Threading
                        Imports System.Threading.Tasks
                        Imports System.Xml
                        Imports System.Xml.Linq";
            }
        }
    }
}
