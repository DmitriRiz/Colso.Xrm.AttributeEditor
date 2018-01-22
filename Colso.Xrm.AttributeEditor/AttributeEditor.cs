﻿using Colso.Xrm.AttributeEditor.AppCode;
using Colso.Xrm.AttributeEditor.Forms;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Windows.Forms;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Args;
using XrmToolBox.Extensibility.Interfaces;

namespace Colso.Xrm.AttributeEditor
{
    public partial class AttributeEditor : UserControl, IXrmToolBoxPluginControl, IGitHubPlugin, IHelpPlugin, IStatusBarMessenger
    {
        #region Variables

        /// <summary>
        /// Information panel
        /// </summary>
        private Panel informationPanel;

        /// <summary>
        /// Dynamics CRM 2011 organization service
        /// </summary>
        private IOrganizationService service;

        private bool workingstate = false;
        private Dictionary<string, int> lvSortcolumns = new Dictionary<string, int>();

        private System.Drawing.Color ColorCreate = System.Drawing.Color.Green;
        private System.Drawing.Color ColorUpdate = System.Drawing.Color.Blue;
        private System.Drawing.Color ColorDelete = System.Drawing.Color.Red;
        private AttributeEditorViewModel _vm;

        #endregion Variables

        public AttributeEditor()
        {
            InitializeComponent();

            // Setup ViewModel
            _vm = new AttributeEditorViewModel(new TestableMetadataHelper());

            _vm.OnRequestConnection += (sender, args) =>
            {
                if (OnRequestConnection != null)
                {
                    var requestConnectionArgs = new RequestConnectionEventArgs
                    {
                        ActionName = "Load",
                        Control = this
                    };
                    OnRequestConnection(this, requestConnectionArgs);
                }
            };
            _vm.OnEntitiesListChanged += (sender, args) =>
            {
                cmbEntities.Items.Clear();
                cmbEntities.DisplayMember = "DisplayName";
                cmbEntities.ValueMember = "SchemaName";
                cmbEntities.Items.AddRange(_vm.Entities.ToArray());
            };
            _vm.OnWorkingStateChanged += (sender, args) =>
            {
                ManageWorkingState(_vm.WorkingState);
            };
            _vm.OnGetInformationPanel += (sender, args) =>
            {
                args.Panel = InformationPanel.GetInformationPanel(this, args.Message, args.Width, args.Height);
            };
            _vm.OnShowMessageBox += (sender, args) =>
            {
                MessageBox.Show(this, args.Message, args.Caption, args.Buttons, args.Icon);
            };
        }

        #region XrmToolbox

        public event EventHandler OnCloseTool;
        public event EventHandler OnRequestConnection;
        public event EventHandler<StatusBarMessageEventArgs> SendMessageToStatusBar;

        public Image PluginLogo
        {
            get { return null; }
        }

        public IOrganizationService Service
        {
            get { throw new NotImplementedException(); }
        }

        public string HelpUrl
        {
            get
            {
                return "https://github.com/MscrmTools/Colso.Xrm.DataTransporter/wiki";
            }
        }

        public string RepositoryName
        {
            get
            {
                return "Colso.Xrm.AttributeEditor";
            }
        }

        public string UserName
        {
            get
            {
                return "MscrmTools";
            }
        }

        public void ClosingPlugin(PluginCloseInfo info)
        {
            if (info.FormReason != CloseReason.None ||
                info.ToolBoxReason == ToolBoxCloseReason.CloseAll ||
                info.ToolBoxReason == ToolBoxCloseReason.CloseAllExceptActive)
            {
                return;
            }

            info.Cancel = MessageBox.Show(@"Are you sure you want to close this tab?", @"Question", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes;
        }

        public void UpdateConnection(IOrganizationService newService, ConnectionDetail connectionDetail, string actionName = "", object parameter = null)
        {
            service = newService;
            _vm.Service = service;

            // Load entities when connection changes
            var t = _vm.LoadEntities();
        }

        public string GetCompany()
        {
            return GetType().GetCompany();
        }

        public string GetMyType()
        {
            return GetType().FullName;
        }

        public string GetVersion()
        {
            return GetType().Assembly.GetName().Version.ToString();
        }

        #endregion XrmToolbox

        #region Form events

        private void tsbCloseThisTab_Click(object sender, EventArgs e)
        {
            if (OnCloseTool != null)
                OnCloseTool(this, null);
        }

        private void tsbLoadEntities_Click(object sender, EventArgs e)
        {
            var t = _vm.LoadEntities();
        }

        private void tsbPublish_Click(object sender, EventArgs e)
        {
            SaveChanges();
        }

        private void cmbEntities_SelectedIndexChanged(object sender, EventArgs e)
        {
            PopulateAttributes();
        }

        private void lvAttributes_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            SetListViewSorting(lvAttributes, e.Column);
        }

        private void btnImport_Click(object sender, EventArgs e)
        {
            ImportAttributes();
        }

        private void btnExport_Click(object sender, EventArgs e)
        {
            ExportAttributes();
        }

        private void btnSelectTemplate_Click(object sender, EventArgs e)
        {
            OpenFileDialog file = new OpenFileDialog();
            if (file.ShowDialog() == DialogResult.OK)
            {
                txtTemplatePath.Text = file.FileName;
            }
        }

        #endregion Form events

        #region Methods

        private void ManageWorkingState(bool working)
        {
            workingstate = working;
            cmbEntities.Enabled = !working;
            gbSettings.Enabled = !working;
            gbAttributes.Enabled = !working;
            Cursor = working ? Cursors.WaitCursor : Cursors.Default;
        }

        private void PopulateAttributes()
        {
            if (!workingstate)
            {
                // Reinit other controls
                lvAttributes.Items.Clear();

                // Setup Column Headings
                lvAttributes.Columns.Clear();
                lvAttributes.Columns.Add("Action");
                foreach (var column in AttributeMetadataRow.GetColumns())
                    lvAttributes.Columns.Add(column.Header, column.Width);

                if (cmbEntities.SelectedItem != null)
                {
                    var entityitem = (EntityItem)cmbEntities.SelectedItem;

                    if (!string.IsNullOrEmpty(entityitem.LogicalName))
                    {
                        ManageWorkingState(true);

                        // Launch treatment
                        var bwFill = new BackgroundWorker();
                        bwFill.DoWork += (sender, e) =>
                        {
                            // Retrieve 
                            var entity = MetadataHelper.RetrieveEntity(entityitem.LogicalName, service);

                            // Prepare list of items
                            var itemList = new List<ListViewItem>();

                            foreach (AttributeMetadata att in entity.Attributes)
                            {
                                if (att.IsValidForUpdate.Value && att.IsCustomizable.Value)
                                {
                                        var attribute = EntityHelper.GetAttributeFromTypeName(att.AttributeType.Value.ToString());

                                        if (attribute == null)
                                            continue;

                                        attribute.LoadFromAttributeMetadata(att);

                                        var row = attribute.ToAttributeMetadataRow();
                                        var item = row.ToListViewItem();

                                        item.Tag = att;
                                        itemList.Add(item);
                                }
                            }

                            e.Result = itemList;
                        };
                        bwFill.RunWorkerCompleted += (sender, e) =>
                        {
                            if (e.Error != null)
                            {
                                MessageBox.Show(this, "An error occured: " + e.Error.Message, "Error", MessageBoxButtons.OK,
                                                MessageBoxIcon.Error);
                            }
                            else
                            {
                                var items = (List<ListViewItem>)e.Result;
                                if (items.Count == 0)
                                {
                                    MessageBox.Show(this, "The entity does not contain any attributes", "Warning", MessageBoxButtons.OK,
                                                    MessageBoxIcon.Warning);
                                }
                                else
                                {
                                    lvAttributes.Items.AddRange(items.ToArray());
                                    SendMessageToStatusBar(this, new StatusBarMessageEventArgs(string.Format("{0} attributes loaded", items.Count)));
                                }
                            }

                            ManageWorkingState(false);
                        };
                        bwFill.RunWorkerAsync();
                    }
                }
            }
        }

        private void ExportAttributes()
        {
            if (cmbEntities.SelectedItem == null)
            {
                MessageBox.Show("You must select an entity", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ManageWorkingState(true);

            var saveFileDlg = new SaveFileDialog()
            {
                Title = "Save the entity template",
                Filter = "Excel Workbook|*.xlsx",
                FileName = "attributeeditor.xlsx"
            };
            saveFileDlg.ShowDialog();

            var selectedLogicalName = (EntityItem) cmbEntities.SelectedItem;

            // If the file name is not an empty string open it for saving.
            if (!string.IsNullOrEmpty(saveFileDlg.FileName))
            {
                informationPanel = InformationPanel.GetInformationPanel(this, "Exporting attributes...", 340, 150);
                SendMessageToStatusBar(this, new StatusBarMessageEventArgs("Initializing attributes..."));

                var bwTransferData = new BackgroundWorker { WorkerReportsProgress = true };
                bwTransferData.DoWork += (sender, e) =>
                {
                    var attributes = (List<AttributeMetadata>)e.Argument;
                    var entityitem = selectedLogicalName;
                    var errors = new List<Tuple<string, string>>();

                    try
                    {
                        using (SpreadsheetDocument document = SpreadsheetDocument.Create(saveFileDlg.FileName, SpreadsheetDocumentType.Workbook))
                        {
                            WorkbookPart workbookPart;
                            Sheets sheets;
                            SheetData sheetData;
                            TemplateHelper.InitDocument(document, out workbookPart, out sheets);
                            TemplateHelper.CreateSheet(workbookPart, sheets, entityitem.LogicalName, out sheetData);

                            #region Header
                            
                            var headerRow = new Row();

                            foreach (var field in AttributeMetadataRow.GetColumns())
                                headerRow.AppendChild(TemplateHelper.CreateCell(field.Type, field.Header));

                            sheetData.AppendChild(headerRow);

                            #endregion

                            #region Data

                            foreach (var attribute in attributes)
                            {
                                if (attribute == null)
                                    continue;

                                var attributeType =
                                    EntityHelper.GetAttributeFromTypeName(attribute.AttributeType.Value.ToString());

                                if (attributeType == null)
                                    continue;

                                attributeType.LoadFromAttributeMetadata(attribute);

                                var metadataRow = attributeType.ToAttributeMetadataRow();

                                sheetData.AppendChild(metadataRow.ToTableRow());
                            }

                            #endregion

                            workbookPart.Workbook.Save();
                        }
                    }
                    catch (FaultException<OrganizationServiceFault> error)
                    {
                        errors.Add(new Tuple<string, string>(entityitem.LogicalName, error.Message));
                    }

                    e.Result = errors;
                };
                bwTransferData.RunWorkerCompleted += (sender, e) =>
                {
                    Controls.Remove(informationPanel);
                    informationPanel.Dispose();
                    SendMessageToStatusBar(this, new StatusBarMessageEventArgs(string.Empty));
                    ManageWorkingState(false);

                    var errors = (List<Tuple<string, string>>)e.Result;

                    if (errors.Count > 0)
                    {
                        var errorDialog = new ErrorList((List<Tuple<string, string>>)e.Result);
                        errorDialog.ShowDialog(ParentForm);
                    }
                };
                bwTransferData.ProgressChanged += (sender, e) =>
                {
                    InformationPanel.ChangeInformationPanelMessage(informationPanel, e.UserState.ToString());
                    SendMessageToStatusBar(this, new StatusBarMessageEventArgs(e.UserState.ToString()));
                };
                bwTransferData.RunWorkerAsync(lvAttributes.Items.Cast<ListViewItem>().Select(v => (AttributeMetadata)v.Tag).ToList());
            }
        }

        private void ImportAttributes()
        {
            if (cmbEntities.SelectedItem == null)
            {
                MessageBox.Show("You must select an entity", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(txtTemplatePath.Text))
            {
                MessageBox.Show("You must select a template file", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ManageWorkingState(true);

            informationPanel = InformationPanel.GetInformationPanel(this, "Loading template...", 340, 150);
            SendMessageToStatusBar(this, new StatusBarMessageEventArgs("Loading template..."));

            var entityItem = (EntityItem) cmbEntities.SelectedItem;
            var items = new List<ListViewItem>();

            var bwTransferData = new BackgroundWorker { WorkerReportsProgress = true };
            bwTransferData.DoWork += (sender, e) =>
            {
                var attributes = (List<ListViewItem>)e.Argument;
                var entityitem = entityItem;
                var errors = new List<Tuple<string, string>>();
                var nrOfCreates = 0;
                var nrOfUpdates = 0;
                var nrOfDeletes = 0;

                try
                {
                    using (FileStream stream = File.Open(txtTemplatePath.Text, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (SpreadsheetDocument document = SpreadsheetDocument.Open(stream, false))
                    {
                        var sheet = document.WorkbookPart.Workbook.Sheets.Cast<Sheet>().Where(s => s.Name == entityitem.LogicalName).FirstOrDefault();
                        if (sheet == null)
                        {
                            errors.Add(new Tuple<string, string>(entityitem.LogicalName, "Entity sheet is not found in the template!"));
                        }
                        else
                        {
                            var sheetid = sheet.Id;
                            var sheetData = ((WorksheetPart)document.WorkbookPart.GetPartById(sheetid)).Worksheet.GetFirstChild<SheetData>();
                            var templaterows = sheetData.ChildElements.Cast<Row>().ToArray();
                            // For shared strings, look up the value in the shared strings table.
                            var stringTable = document.WorkbookPart.GetPartsOfType<SharedStringTablePart>().FirstOrDefault()?.SharedStringTable;

                            // Check all existing attributes
                            foreach (var item in attributes)
                            {
                                var row = templaterows.Where(r => TemplateHelper.GetCellValue(r, 0, stringTable) == item.SubItems[1].Text).FirstOrDefault();

                                var attributeMetadataRow = AttributeMetadataRow.FromListViewItem(item);

                                attributeMetadataRow.UpdateFromTemplateRow(row, stringTable);
                                var listViewItem = attributeMetadataRow.ToListViewItem();
                                listViewItem.Tag = item.Tag;

                                items.Add(listViewItem);

                                if (attributeMetadataRow.Action == "Update")
                                    nrOfUpdates++;
                                else if (attributeMetadataRow.Action == "Delete")
                                    nrOfDeletes++;

                                InformationPanel.ChangeInformationPanelMessage(informationPanel, string.Format("Processing new attribute {0}...", item.Text));
                            }

                            // Check new attributes
                            for (int i = 1; i < templaterows.Length; i++)
                            {
                                var row = templaterows[i];
                                var logicalname = TemplateHelper.GetCellValue(row, 0, stringTable);

                                if (!string.IsNullOrEmpty(logicalname))
                                {
                                    var attribute = attributes.Select(a => (AttributeMetadata)a.Tag).Where(a => a.LogicalName == logicalname).FirstOrDefault();

                                    if (attribute == null)
                                    {
                                        InformationPanel.ChangeInformationPanelMessage(informationPanel, string.Format("Processing new attribute {0}...", logicalname));

                                        var attributeMetadataRow = AttributeMetadataRow.FromTableRow(row, stringTable);
                                        attributeMetadataRow.Action = "Create";

                                        items.Add(attributeMetadataRow.ToListViewItem());

                                        nrOfCreates++;
                                    }
                                }
                            }
                        }
                    }
                    SendMessageToStatusBar(this, new StatusBarMessageEventArgs(string.Format("{0} create; {1} update; {2} delete", nrOfCreates, nrOfUpdates, nrOfDeletes)));
                }
                catch (FaultException<OrganizationServiceFault> error)
                {
                    errors.Add(new Tuple<string, string>(entityitem.LogicalName, error.Message));
                    SendMessageToStatusBar(this, new StatusBarMessageEventArgs(string.Empty));
                }

                e.Result = errors;
            };
            bwTransferData.RunWorkerCompleted += (sender, e) =>
            {
                lvAttributes.BeginUpdate();

                // Add attributes here to avoid accessing lvAttributes across threads.
                lvAttributes.Items.Clear();
                foreach (var item in items)
                    lvAttributes.Items.Add(item);

                lvAttributes.EndUpdate();

                Controls.Remove(informationPanel);
                informationPanel.Dispose();
                ManageWorkingState(false);

                var errors = (List<Tuple<string, string>>)e.Result;

                if (errors.Count > 0)
                {
                    var errorDialog = new ErrorList((List<Tuple<string, string>>)e.Result);
                    errorDialog.ShowDialog(ParentForm);
                }
            };
            bwTransferData.ProgressChanged += (sender, e) =>
            {
                InformationPanel.ChangeInformationPanelMessage(informationPanel, e.UserState.ToString());
                SendMessageToStatusBar(this, new StatusBarMessageEventArgs(e.UserState.ToString()));
            };
            bwTransferData.RunWorkerAsync(lvAttributes.Items.Cast<ListViewItem>().ToList());
        }

        private void SaveChanges()
        {
            if (cmbEntities.SelectedItem == null)
            {
                MessageBox.Show("You must select an entity", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ManageWorkingState(true);
            informationPanel = InformationPanel.GetInformationPanel(this, "Processing entity...", 340, 150);

            var entityitem = (EntityItem) cmbEntities.SelectedItem;
            var itemsToProcess = lvAttributes.Items.Cast<ListViewItem>().ToList();

            var bwTransferData = new BackgroundWorker { WorkerReportsProgress = true };
            bwTransferData.DoWork += (sender, e) =>
            {
                var errors = new List<Tuple<string, string>>();
                var helper = new EntityHelper(entityitem.LogicalName, entityitem.LanguageCode, service);

                foreach (var item in itemsToProcess)
                {
                    // TODO: Validate each row
                    //if (item.SubItems.Count == 6)
                    //{ 
                        var action = item.SubItems[0].Text;

                        if (!string.IsNullOrEmpty(action) && !string.IsNullOrEmpty(item.Text))
                        {
                            var attributeMetadata = AttributeMetadataRow.FromListViewItem(item);

                            InformationPanel.ChangeInformationPanelMessage(informationPanel, string.Format("Processing attribute {0}...", attributeMetadata.LogicalName));

                            try
                            {
                                var attribute = EntityHelper.GetAttributeFromTypeName(attributeMetadata.AttributeType);

                                if (attribute == null)
                                {
                                    errors.Add(new Tuple<string, string>(item.Text,
                                        $"The Attribute Type \"{attributeMetadata.AttributeType}\" is not yet supported."));
                                    continue;
                                }

                                attribute.LoadFromAttributeMetadataRow(attributeMetadata);
                                attribute.Entity = entityitem.LogicalName;

                                switch (action)
                                {
                                    case "Create":
                                        if (cbCreate.Checked) attribute.CreateAttribute(service);
                                        break;
                                    case "Update":
                                        if (cbUpdate.Checked) attribute.UpdateAttribute(service);
                                        break;
                                    case "Delete":
                                        if (cbDelete.Checked) attribute.DeleteAttribute(service);
                                        break;
                                }
                            }
                            catch (FaultException<OrganizationServiceFault> error)
                            {
                                errors.Add(new Tuple<string, string>(item.Text, error.Message));
                            }
                        }
                    //}
                    //else
                    //{
                    //    errors.Add(new Tuple<string, string>(item.Name, string.Format("Invalid row! Unexpected subitems count [{0}]", item.SubItems.Count)));
                    //}
                }

                helper.Publish();
                e.Result = errors;
            };
            bwTransferData.RunWorkerCompleted += (sender, e) =>
            {
                Controls.Remove(informationPanel);
                informationPanel.Dispose();
                SendMessageToStatusBar(this, new StatusBarMessageEventArgs(string.Empty));
                ManageWorkingState(false);

                var errors = (List<Tuple<string, string>>)e.Result;

                if (errors.Count > 0)
                {
                    var errorDialog = new ErrorList((List<Tuple<string, string>>)e.Result);
                    errorDialog.ShowDialog(ParentForm);
                }

                PopulateAttributes();
            };
            bwTransferData.ProgressChanged += (sender, e) =>
            {
                InformationPanel.ChangeInformationPanelMessage(informationPanel, e.UserState.ToString());
                SendMessageToStatusBar(this, new StatusBarMessageEventArgs(e.UserState.ToString()));
            };
            bwTransferData.RunWorkerAsync();
        }

        private void Entity_OnStatusMessage(object sender, EventArgs e)
        {
            SendMessageToStatusBar(this, new StatusBarMessageEventArgs(((StatusMessageEventArgs)e).Message));
        }

        private void SetListViewSorting(ListView listview, int column)
        {
            int currentSortcolumn = -1;
            if (lvSortcolumns.ContainsKey(listview.Name))
                currentSortcolumn = lvSortcolumns[listview.Name];
            else
                lvSortcolumns.Add(listview.Name, currentSortcolumn);

            if (currentSortcolumn != column)
            {
                lvSortcolumns[listview.Name] = column;
                listview.Sorting = SortOrder.Ascending;
            }
            else
            {
                if (listview.Sorting == SortOrder.Ascending)
                    listview.Sorting = SortOrder.Descending;
                else
                    listview.Sorting = SortOrder.Ascending;
            }

            listview.ListViewItemSorter = new ListViewItemComparer(column, listview.Sorting);
        }

        private bool IsChanged(ListViewItem lvItems, Row row, SharedStringTable stringTable)
        {
            var changed = false;

            var displayName = TemplateHelper.GetCellValue(row, 1, stringTable);
            if (lvItems.SubItems[1].Text != displayName && !string.IsNullOrEmpty(displayName))
            {
                lvItems.SubItems[1].Text = displayName;
                lvItems.SubItems[1].ForeColor = ColorUpdate;
                changed = true;
            }

            for (var i = 4; i < lvItems.SubItems.Count; i++)
            {
                var item = lvItems.SubItems[i];

                var value = TemplateHelper.GetCellValue(row, 4, stringTable);

                if (item.Text != value && !string.IsNullOrEmpty(value))
                {
                    item.Text = value;
                    item.ForeColor = ColorUpdate;
                    changed = true;
                }
            }

            return changed;
        }

        #endregion Methods

    }
}