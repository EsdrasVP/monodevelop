//
// FixAllPreviewDialog.cs
//
// Author:
//       Marius Ungureanu <maungu@microsoft.com>
//
// Copyright (c) 2018 Microsoft Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Editor;
using Microsoft.CodeAnalysis.Text;
using MonoDevelop.Core;
using MonoDevelop.Core.Text;
using MonoDevelop.Ide;
using MonoDevelop.Ide.Composition;
using MonoDevelop.Ide.Editor;
using MonoDevelop.Refactoring;
using Xwt;
using Xwt.Drawing;
using TextChange = Microsoft.CodeAnalysis.Text.TextChange;

namespace MonoDevelop.CodeActions
{
	class FixAllPreviewDialog : Dialog
	{
		readonly DataField<bool> nodeCheck = new DataField<bool> ();
		readonly DataField<string> nodeLabel = new DataField<string> ();
		readonly DataField<Image> nodeIcon = new DataField<Image> ();
		readonly DataField<bool> nodeIconVisible = new DataField<bool> ();
		readonly DataField<TextChange> nodeEditor = new DataField<TextChange> ();
		readonly TreeView treeView;
		readonly TreeStore store;
		readonly TextEditor baseEditor, changedEditor;
		readonly ImmutableArray<CodeActionOperation> operations;
		CheckBoxCellView checkBox;

		public FixAllPreviewDialog (string diagnosticId, string scopeLabel, FixAllScope scope, ImmutableArray<CodeActionOperation> operations, TextEditor baseEditor)
		{
			Width = 800;
			Height = 600;

			this.baseEditor = baseEditor;
			this.operations = operations;

			// TODO: checkbox dependencies
			store = new TreeStore (nodeCheck, nodeLabel, nodeIcon, nodeIconVisible, nodeEditor);
			treeView = new TreeView {
				DataSource = store,
				HeadersVisible = false,
			};
			changedEditor = TextEditorFactory.CreateNewEditor (baseEditor.FileName, baseEditor.MimeType);
			changedEditor.IsReadOnly = true;

			if (scope != FixAllScope.Document) {
				// No implementation yet for project/solution scopes
				// Requires global undo and a lot more work
				throw new NotImplementedException ();
			}

			var mainBox = new VBox {
				MarginTop = 6,
				MarginLeft = 8,
				MarginRight = 8,
				MarginBottom = 8,
			};

			// scopeLabel can be document filename, project/solution name.
			var treeViewHeaderText = GettextCatalog.GetString (
				"Fix all '{0}' in '{1}'",
				string.Format ("<b>{0}</b>", diagnosticId),
				string.Format ("<b>{0}</b>", scopeLabel)
			);

			mainBox.PackStart (new Label {
				Markup = treeViewHeaderText,
			});

			var col = new ListViewColumn ();
			checkBox = new CheckBoxCellView (nodeCheck) {
				Editable = true,
			};
			checkBox.Toggled += OnCheckboxToggled;
			col.Views.Add (checkBox);
			col.Views.Add (new ImageCellView (nodeIcon) {
				VisibleField = nodeIconVisible,
			});
			col.Views.Add (new TextCellView (nodeLabel));
			treeView.Columns.Add (col);

			treeView.SelectionChanged += OnSelectionChanged;

			mainBox.PackStart (new ScrollView (treeView), true);

			var previewHeaderText = GettextCatalog.GetString ("Preview Code Changes:");
			mainBox.PackStart (new Label (previewHeaderText) {
				MarginTop = 12,
			});

			mainBox.PackStart (new FrameBox (changedEditor) {
				BorderWidth = 2,
				BorderColor = Ide.Gui.Styles.StatusBarBorderColor,
			}, true);

			Content = mainBox;
			Buttons.Add (Command.Cancel, Command.Apply);

			var rootNode = store.AddNode ();
			var fixAllOccurencesText = GettextCatalog.GetString ("Fix all occurences");
			rootNode.SetValues (nodeCheck, true, nodeLabel, fixAllOccurencesText, nodeIcon, ImageService.GetIcon ("md-csharp-file"), nodeIconVisible, true);
		}

		void OnCheckboxToggled (object sender, WidgetEventArgs args)
		{
			// FIXME: this can race with selection change.
			OnSelectionChanged (sender, args);
		}

		protected override void Dispose (bool disposing)
		{
			if (checkBox != null) {
				checkBox.Toggled -= OnCheckboxToggled;
				checkBox = null;
			}
			base.Dispose (disposing);
		}

		public async Task InitializeEditor ()
		{
			var rootNode = store.GetFirstNode ();
			if (rootNode == null)
				return;

			var baseText = await baseEditor.DocumentContext.AnalysisDocument.GetTextAsync ();
			foreach (var operation in operations) {
				if (!(operation is ApplyChangesOperation ac)) {
					continue;
				}

				var changedDocument = ac.ChangedSolution.GetDocument (baseEditor.DocumentContext.AnalysisDocument.Id);
				var newText = await changedDocument.GetTextAsync ();

				var diff = newText.GetTextChanges (baseText);
				foreach (var change in diff) {
					var node = rootNode.Clone ();

					var line = newText.Lines.GetLineFromPosition (change.Span.Start);
					var length = Math.Max (line.Span.Length, change.NewText.Length);
					var span = new TextSpan (line.Start, length);
					var newSubText = newText.GetSubText (span).ToString ().Replace(Environment.NewLine, "…").Trim ();

					var operationNode = node.AddChild ();
					operationNode.SetValues (nodeCheck, true, nodeLabel, newSubText, nodeEditor, change);
				}
			}

			treeView.ExpandAll ();
		}

		public IEnumerable<TextChange> GetApplicableChanges ()
		{
			var rootNode = store.GetFirstNode ();
			if (rootNode == null || !rootNode.MoveToChild ())
				yield break;

			do {
				var check = rootNode.GetValue (nodeCheck);
				if (!check)
					continue;

				yield return rootNode.GetValue (nodeEditor);
			} while (rootNode.MoveNext ()); 
		}

		async void OnSelectionChanged (object sender, EventArgs args)
		{
			var node = store.GetNavigatorAt (treeView.SelectedRow);
			if (node == null)
				return;

			var baseText = await baseEditor.DocumentContext.AnalysisDocument.GetTextAsync ();
			var isOn = node.GetValue (nodeCheck);
			if (!isOn) {
				SetChangedEditorText (baseText, Enumerable.Empty<TextChange> ());
				return;
			}

			var textChange = node.GetValue (nodeEditor);
			if (textChange == default) {
				SetChangedEditorText (baseText, GetApplicableChanges ());
				return;
			}
			SetChangedEditorText (baseText, Enumerable.Repeat (textChange, 1));
		}

		void SetChangedEditorText (SourceText baseText, IEnumerable<TextChange> textChanges)
		{
			changedEditor.Text = baseText.WithChanges (textChanges).ToString ();

			var first = textChanges.FirstOrDefault ();
			if (first == default)
				return;

			changedEditor.CenterTo (first.Span.Start);
			changedEditor.SelectionRange = new TextSegment (first.Span.Start, first.NewText.Length);
		}
	}
}
