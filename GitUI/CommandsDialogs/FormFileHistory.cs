using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using GitCommands;
using GitCommands.Utils;
using ResourceManager;
using System.ComponentModel;

namespace GitUI.CommandsDialogs
{
    public sealed partial class FormFileHistory : GitModuleForm
    {
        private readonly ICommitDataManager _commitDataManager;
        private readonly FilterRevisionsHelper _filterRevisionsHelper;
        private readonly FilterBranchHelper _filterBranchHelper;
        private readonly AsyncLoader _asyncLoader;
        private readonly FormBrowseMenus _formBrowseMenus;

        private readonly TranslationString _buildReportTabCaption =
            new TranslationString("Build Report");

        private FormFileHistory()
            : this(null)
        { }

        internal FormFileHistory(GitUICommands aCommands)
            : base(aCommands)
        {
            InitializeComponent();
            _asyncLoader = new AsyncLoader();
            // set tab page images
            {
                var imageList = new ImageList();
                tabControl1.ImageList = imageList;
                imageList.ColorDepth = ColorDepth.Depth8Bit;
                imageList.Images.Add(global::GitUI.Properties.Resources.IconViewFile);
                imageList.Images.Add(global::GitUI.Properties.Resources.IconDiff);
                imageList.Images.Add(global::GitUI.Properties.Resources.IconBlame);
                tabControl1.TabPages[0].ImageIndex = 0;
                tabControl1.TabPages[1].ImageIndex = 1;
                tabControl1.TabPages[2].ImageIndex = 2;
            }

            _filterBranchHelper = new FilterBranchHelper(toolStripBranchFilterComboBox, toolStripBranchFilterDropDownButton, FileChanges);
            _filterRevisionsHelper = new FilterRevisionsHelper(toolStripRevisionFilterTextBox, toolStripRevisionFilterDropDownButton, FileChanges, toolStripRevisionFilterLabel, ShowFirstParent, form: this);

            _formBrowseMenus = new FormBrowseMenus(FileHistoryContextMenu);
            _formBrowseMenus.ResetMenuCommandSets();
            _formBrowseMenus.AddMenuCommandSet(MainMenuItem.NavigateMenu, FileChanges.MenuCommands.GetNavigateMenuCommands());
            _formBrowseMenus.AddMenuCommandSet(MainMenuItem.ViewMenu, FileChanges.MenuCommands.GetViewMenuCommands());
            _formBrowseMenus.InsertAdditionalMainMenuItems(toolStripSeparator4);

            _commitDataManager = new CommitDataManager(() => Module);
        }

        public FormFileHistory(GitUICommands aCommands, string fileName, GitRevision revision, bool filterByRevision)
            : this(aCommands)
        {
            FileChanges.SetInitialRevision(revision);
            Translate();

            FileChanges.ShowBuildServerInfo = true;

            FileName = fileName;
            SetTitle(string.Empty);

            Diff.ExtraDiffArgumentsChanged += DiffExtraDiffArgumentsChanged;

            bool isSubmodule = GitModule.IsValidGitWorkingDir(Path.Combine(Module.WorkingDir, FileName));
            if (revision != null && revision.IsArtificial() || isSubmodule) //no blame for artificial
                tabControl1.RemoveIfExists(BlameTab);
            FileChanges.SelectionChanged += FileChangesSelectionChanged;
            FileChanges.DisableContextMenu();

            UpdateFollowHistoryMenuItems();
            fullHistoryToolStripMenuItem.Checked = AppSettings.FullHistoryInFileHistory;
            ShowFullHistory.Checked = AppSettings.FullHistoryInFileHistory;
            loadHistoryOnShowToolStripMenuItem.Checked = AppSettings.LoadFileHistoryOnShow;
            loadBlameOnShowToolStripMenuItem.Checked = AppSettings.LoadBlameOnShow && tabControl1.Contains(BlameTab);
            saveAsToolStripMenuItem.Visible = !isSubmodule;

            if (filterByRevision && revision != null && revision.Guid != null)
                _filterBranchHelper.SetBranchFilter(revision.Guid, false);
        }

        public FormFileHistory(GitUICommands aCommands, string fileName)
            : this(aCommands, fileName, null, false)
        {
        }

        protected override void OnRuntimeLoad(EventArgs e)
        {
            base.OnRuntimeLoad(e);

            bool autoLoad = (tabControl1.SelectedTab == BlameTab && AppSettings.LoadBlameOnShow) || AppSettings.LoadFileHistoryOnShow;

            if (autoLoad)
                LoadFileHistory();
            else
                FileChanges.Visible = false;
        }

        private string FileName { get; set; }

        public void SelectBlameTab()
        {
            tabControl1.SelectedTab = BlameTab;
        }

        public void SelectDiffTab()
        {
            tabControl1.SelectedTab = DiffTab;
        }

        private void LoadFileHistory()
        {
            FileChanges.Visible = true;

            _asyncLoader.Load(() => BuildFilter(FileName), (filter) =>
            {
                if (filter == null)
                    return;
                FileChanges.FixedRevisionFilter = filter.RevisionFilter;
                FileChanges.FixedPathFilter = filter.PathFilter;
                FileChanges.FiltredFileName = FileName;
                FileChanges.AllowGraphWithFilter = true;
                FileChanges.Load();
            });
        }

        private class FixedFilterTuple
        {
            public string RevisionFilter;
            public string PathFilter;
        }

        private FixedFilterTuple BuildFilter(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return null;

            //Replace windows path separator to Linux path separator.
            //This is needed to keep the file history working when started from file tree in
            //browse dialog.
            fileName = fileName.ToPosixPath();

            // we will need this later to look up proper casing for the file
            var fullFilePath = Path.Combine(Module.WorkingDir, fileName);

            //The section below contains native windows (kernel32) calls
            //and breaks on Linux. Only use it on Windows. Casing is only
            //a Windows problem anyway.
            if (EnvUtils.RunningOnWindows() && File.Exists(fullFilePath))
            {
                // grab the 8.3 file path
                var shortPath = new StringBuilder(4096);
                NativeMethods.GetShortPathName(fullFilePath, shortPath, shortPath.Capacity);

                // use 8.3 file path to get properly cased full file path
                var longPath = new StringBuilder(4096);
                NativeMethods.GetLongPathName(shortPath.ToString(), longPath, longPath.Capacity);

                // remove the working directory and now we have a properly cased file name.
                fileName = longPath.ToString().Substring(Module.WorkingDir.Length);
            }

            if (fileName.StartsWith(Module.WorkingDir, StringComparison.InvariantCultureIgnoreCase))
                fileName = fileName.Substring(Module.WorkingDir.Length);

            FileName = fileName;

            FixedFilterTuple res = new FixedFilterTuple();
            res.PathFilter = " \"" + fileName + "\"";
            if (AppSettings.FollowRenamesInFileHistory && !Directory.Exists(fullFilePath))
            {
                // git log --follow is not working as expected (see  http://kerneltrap.org/mailarchive/git/2009/1/30/4856404/thread)
                //
                // But we can take a more complicated path to get reasonable results:
                //  1. use git log --follow to get all previous filenames of the file we are interested in
                //  2. use git log "list of files names" to get the history graph
                //
                // note: This implementation is quite a quick hack (by someone who does not speak C# fluently).
                //

                string arg = "log --format=\"%n\" --name-only --follow "+
                    GitCommandHelpers.FindRenamesAndCopiesOpts()
                    + " -- \"" + fileName + "\"";
                Process p = Module.RunGitCmdDetached(arg, GitModule.LosslessEncoding);

                // the sequence of (quoted) file names - start with the initial filename for the search.
                var listOfFileNames = new StringBuilder("\"" + fileName + "\"");

                // keep a set of the file names already seen
                var setOfFileNames = new HashSet<string> { fileName };

                string line;
                do
                {
                    line = p.StandardOutput.ReadLine();
                    line = GitModule.ReEncodeFileNameFromLossless(line);

                    if (!string.IsNullOrEmpty(line) && setOfFileNames.Add(line))
                    {
                        listOfFileNames.Append(" \"");
                        listOfFileNames.Append(line);
                        listOfFileNames.Append('\"');
                    }
                } while (line != null);
                // here we need --name-only to get the previous filenames in the revision graph
                res.PathFilter = listOfFileNames.ToString();
                res.RevisionFilter += " --name-only --parents" + GitCommandHelpers.FindRenamesAndCopiesOpts();
            }
            else if (AppSettings.FollowRenamesInFileHistory)
            {
                // history of a directory
                // --parents doesn't work with --follow enabled, but needed to graph a filtered log
                res.RevisionFilter = " " + GitCommandHelpers.FindRenamesOpt() + " --follow --parents";
            }
            else
            {
                // rename following disabled
                res.RevisionFilter = " --parents";
            }

            if (AppSettings.FullHistoryInFileHistory)
            {
                res.RevisionFilter = string.Concat(" --full-history --simplify-merges ", res.RevisionFilter);
            }


            return res;
        }

        private void DiffExtraDiffArgumentsChanged(object sender, EventArgs e)
        {
            UpdateSelectedFileViewers();
        }

        private void FileChangesSelectionChanged(object sender, EventArgs e)
        {
            View.SaveCurrentScrollPos();
            Diff.SaveCurrentScrollPos();

            var selectedRows = FileChanges.GetSelectedRevisions();
            if (selectedRows.Count > 0)
            {
                bool isSubmodule = GitModule.IsValidGitWorkingDir(Path.Combine(Module.WorkingDir, FileName));
                GitRevision revision = selectedRows[0];
                if (revision.IsArtificial() || isSubmodule)
                    tabControl1.RemoveIfExists(BlameTab);
                else
                    tabControl1.InsertIfNotExists(2, BlameTab);
            }
            UpdateSelectedFileViewers();
        }

        private void SetTitle(string fileName)
        {
            Text = string.Format("File History - {0}", FileName);
            if (!fileName.IsNullOrEmpty() && !fileName.Equals(FileName))
                Text = Text + string.Format(" ({0})", fileName);

            Text += " - " + Module.WorkingDir;
        }

        private void UpdateSelectedFileViewers()
        {
            var selectedRows = FileChanges.GetSelectedRevisions();

            if (selectedRows.Count == 0) return;

            GitRevision revision = selectedRows[0];
            var children = FileChanges.GetRevisionChildren(revision.Guid);

            var fileName = revision.Name;

            if (string.IsNullOrEmpty(fileName))
                fileName = FileName;

            SetTitle(fileName);

            if (tabControl1.SelectedTab == BlameTab)
                Blame.LoadBlame(revision, children, fileName, FileChanges, BlameTab, Diff.Encoding);
            else if (tabControl1.SelectedTab == ViewTab)
            {
                var scrollpos = View.ScrollPos;

                View.Encoding = Diff.Encoding;
                View.ViewGitItemRevision(fileName, revision.Guid);
                View.ScrollPos = scrollpos;
            }
            else if (tabControl1.SelectedTab == DiffTab)
            {
                GitItemStatus file = new GitItemStatus();
                file.IsTracked = true;
                file.Name = fileName;
                file.IsSubmodule = GitModule.IsValidGitWorkingDir(Path.Combine(Module.WorkingDir, fileName));
                Diff.ViewChanges(FileChanges.GetSelectedRevisions(), file, "You need to select at least one revision to view diff.");
            }

            if (!EnvUtils.IsMonoRuntime())
            {
                if (_buildReportTabPageExtension == null)
                    _buildReportTabPageExtension = new BuildReportTabPageExtension(tabControl1, _buildReportTabCaption.Text);

                _buildReportTabPageExtension.FillBuildReport(selectedRows.Count == 1 ? revision : null);
            }
        }

        private BuildReportTabPageExtension _buildReportTabPageExtension;

        private void TabControl1SelectedIndexChanged(object sender, EventArgs e)
        {
            FileChangesSelectionChanged(sender, e);
        }

        private void FileChangesDoubleClick(object sender, EventArgs e)
        {
            FileChanges.ViewSelectedRevisions();
        }

        private void OpenWithDifftoolToolStripMenuItemClick(object sender, EventArgs e)
        {
            var selectedRows = FileChanges.GetSelectedRevisions();

            string orgFileName = null;
            if (selectedRows.Count > 0)
            {
                orgFileName = selectedRows[0].Name;
            }
            FileChanges.OpenWithDifftool(FileName, orgFileName, GitUI.RevisionDiffKind.DiffAB);
        }

        private void saveAsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selectedRows = FileChanges.GetSelectedRevisions();

            if (selectedRows.Count > 0)
            {
                string orgFileName = selectedRows[0].Name;

                if (string.IsNullOrEmpty(orgFileName))
                    orgFileName = FileName;

                string fullName = Module.WorkingDir + orgFileName.ToNativePath();

                using (var fileDialog = new SaveFileDialog
                {
                    InitialDirectory = Path.GetDirectoryName(fullName),
                    FileName = Path.GetFileName(fullName),
                    DefaultExt = GitCommandHelpers.GetFileExtension(fullName),
                    AddExtension = true
                })
                {
                    fileDialog.Filter =
                        "Current format (*." +
                        fileDialog.DefaultExt + ")|*." +
                        fileDialog.DefaultExt +
                        "|All files (*.*)|*.*";
                    if (fileDialog.ShowDialog(this) == DialogResult.OK)
                    {
                        Module.SaveBlobAs(fileDialog.FileName, selectedRows[0].Guid + ":\"" + orgFileName + "\"");
                    }
                }
            }
        }

        private void followFileHistoryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AppSettings.FollowRenamesInFileHistory = !AppSettings.FollowRenamesInFileHistory;
            UpdateFollowHistoryMenuItems();

            LoadFileHistory();
        }

        private void UpdateFollowHistoryMenuItems()
        {
            followFileHistoryToolStripMenuItem.Checked = AppSettings.FollowRenamesInFileHistory;
            followFileHistoryRenamesToolStripMenuItem.Enabled = AppSettings.FollowRenamesInFileHistory;
            followFileHistoryRenamesToolStripMenuItem.Checked = AppSettings.FollowRenamesInFileHistoryExactOnly;
        }

        private void fullHistoryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ToggleFullHistoryFlag();
        }

        private void ShowFullHistory_Click(object sender, EventArgs e)
        {
            ToggleFullHistoryFlag();
        }

        private void ToggleFullHistoryFlag()
        {
            AppSettings.FullHistoryInFileHistory = !AppSettings.FullHistoryInFileHistory;
            fullHistoryToolStripMenuItem.Checked = AppSettings.FullHistoryInFileHistory;
            ShowFullHistory.Checked = AppSettings.FullHistoryInFileHistory;
            LoadFileHistory();
        }

        private void cherryPickThisCommitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selectedRevisions = FileChanges.GetSelectedRevisions();
            if (selectedRevisions.Count == 1)
            {
                UICommands.StartCherryPickDialog(this, selectedRevisions[0]);
            }
        }

        private void revertCommitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selectedRevisions = FileChanges.GetSelectedRevisions();
            if (selectedRevisions.Count == 1)
            {
                UICommands.StartRevertCommitDialog(this, selectedRevisions[0]);
            }
        }

        private void viewCommitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            FileChanges.ViewSelectedRevisions();
        }

        private void FileHistoryContextMenuOpening(object sender, CancelEventArgs e)
        {
            var selectedRevisions = FileChanges.GetSelectedRevisions();

            diffToolremotelocalStripMenuItem.Enabled =
                selectedRevisions.Count == 1 && selectedRevisions[0].Guid != GitRevision.UnstagedGuid && File.Exists(FileName);
            openWithDifftoolToolStripMenuItem.Enabled =
                selectedRevisions.Count >= 1 && selectedRevisions.Count <= 2;
            manipuleerCommitToolStripMenuItem.Enabled =
                viewCommitToolStripMenuItem.Enabled =
                selectedRevisions.Count == 1 && !selectedRevisions[0].IsArtificial();
            saveAsToolStripMenuItem.Enabled = selectedRevisions.Count == 1;
        }

        private const string FormBrowseName = "FormBrowse";

        public override void AddTranslationItems(ITranslation translation)
        {
            base.AddTranslationItems(translation);
            TranslationUtils.AddTranslationItemsFromFields(FormBrowseName, _filterRevisionsHelper, translation);
            TranslationUtils.AddTranslationItemsFromFields(FormBrowseName, _filterBranchHelper, translation);
        }

        public override void TranslateItems(ITranslation translation)
        {
            base.TranslateItems(translation);
            TranslationUtils.TranslateItemsFromFields(FormBrowseName, _filterRevisionsHelper, translation);
            TranslationUtils.TranslateItemsFromFields(FormBrowseName, _filterBranchHelper, translation);
        }

        private void diffToolremotelocalStripMenuItem_Click(object sender, EventArgs e)
        {
            FileChanges.OpenWithDifftool(FileName, string.Empty, GitUI.RevisionDiffKind.DiffBLocal);
        }

        private void toolStripSplitLoad_ButtonClick(object sender, EventArgs e)
        {
            LoadFileHistory();
        }

        private void loadHistoryOnShowToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AppSettings.LoadFileHistoryOnShow = !AppSettings.LoadFileHistoryOnShow;
            loadHistoryOnShowToolStripMenuItem.Checked = AppSettings.LoadFileHistoryOnShow;
        }

        private void loadBlameOnShowToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AppSettings.LoadBlameOnShow = !AppSettings.LoadBlameOnShow;
            loadBlameOnShowToolStripMenuItem.Checked = AppSettings.LoadBlameOnShow;
        }

        private void Blame_CommandClick(object sender, CommitInfo.CommandEventArgs e)
        {
            if (e.Command == "gotocommit")
            {
                FileChanges.SetSelectedRevision(GitRevision.CreateForShortSha1(Module, e.Data));
            }
            else if (e.Command == "gotobranch" || e.Command == "gototag")
            {
                string error = "";
                CommitData commit = _commitDataManager.GetCommitData(e.Data, ref error);
                if (commit != null)
                    FileChanges.SetSelectedRevision(new GitRevision(Module, commit.Guid));
            }
            else if (e.Command == "navigatebackward")
            {
                FileChanges.NavigateBackward();
            }
            else if (e.Command == "navigateforward")
            {
                FileChanges.NavigateForward();
            }
        }

        private void followFileHistoryRenamesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AppSettings.FollowRenamesInFileHistoryExactOnly = !AppSettings.FollowRenamesInFileHistoryExactOnly;
            UpdateFollowHistoryMenuItems();
            LoadFileHistory();
        }

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _asyncLoader.Cancel();
                _asyncLoader.Dispose();
                _filterRevisionsHelper.Dispose();
                _filterBranchHelper.Dispose();
                _formBrowseMenus.Dispose();

                if (components != null)
                    components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void toolStripBranchFilterComboBox_Click(object sender, EventArgs e)
        {
            toolStripBranchFilterComboBox.DroppedDown = true;
        }
    }
}