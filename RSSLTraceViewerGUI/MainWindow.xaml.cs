﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RDMDictDecoder;
using RSSLTraceDecoder;
using RSSLTraceDecoder.MRN;
using static RSSLTraceDecoder.Utils.RdmDataConverter;
using Formatting = Newtonsoft.Json.Formatting;

namespace RSSLTraceViewerGUI
{
    /// <summary>
    ///     Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<XMLFragmentsData> _fragmentCollection =
            new ObservableCollection<XMLFragmentsData>();

        public readonly ObservableCollection<CheckBoxStringItem> DomainCheckBoxItems =
            new ObservableCollection<CheckBoxStringItem>();

        public readonly ObservableCollection<CheckBoxStringItem> KeyNameCheckBoxItems =
            new ObservableCollection<CheckBoxStringItem>();

        public readonly ObservableCollection<CheckBoxStringItem> MsgTypeCheckBoxItems =
            new ObservableCollection<CheckBoxStringItem>();

        public readonly ObservableCollection<CheckBoxIntItem> StreamIdCheckBoxItems =
            new ObservableCollection<CheckBoxIntItem>();

        private readonly string _defaultTitle = "RSSL Trace Viewer";


        private RdmEnumTypeDef _enumTypeDef;
        private bool _enumTypeDefDictLoaded;
        private string _enumTypeDefFilePath = string.Empty;
        private RdmFieldDictionary _fieldDictionary;
        private MrnMsgList _messageList = new MrnMsgList();

        //For App config
        private string _rdmDictFilePath = string.Empty;
        private bool _rdmFieldDictLoaded;
        private int _selectedIndex = -1;
        private bool _useRdmStrictMode = true;

        public MainWindow()
        {
            InitializeComponent();
            InitializeControls();
        }

        public XmlFragmentList XmlFragments { get; private set; } = new XmlFragmentList();

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            Application.Current.Shutdown();
        }

        private void InitializeControls()
        {
            if (!File.Exists($"{Environment.CurrentDirectory}\\app.config.json"))
                MessageBox.Show($"Unable to find app.config.json from {Environment.CurrentDirectory}");
            //Read application config
            try
            {
                using (var streamReader = new StreamReader($"{Environment.CurrentDirectory}\\app.config.json"))
                {
                    var textReader = new JsonTextReader(streamReader);
                    var jObject = JObject.Load(textReader);
                    var appConfig = (dynamic) jObject;
                    _rdmDictFilePath = (string) appConfig["RDMDictionary"]["RDMFieldDictFilePath"];
                    _enumTypeDefFilePath = (string) appConfig["RDMDictionary"]["EnumTypeDefFilePath"];
                    
                    _useRdmStrictMode =
                        (bool) (appConfig["UseRDMStrictMode"] == null ? true : appConfig["UseRDMStrictMode"]);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }

            UnpackMrnBtn.IsEnabled = false;
            UnpackMrnBtn.Visibility = Visibility.Hidden;
            _rdmFieldDictLoaded = false;
            _enumTypeDefDictLoaded = false;

            XmlTreeView.IsEnabled = false;
            XmlTreeView.Visibility = Visibility.Collapsed;
            DomainComboBox.IsEnabled = false;
            StreamIdComboBox.IsEnabled = false;
            MsgTypeComboBox.IsEnabled = false;
            KeyNameComboBox.IsEnabled = false;
            Title = _defaultTitle;
        }


        private async void LoadXmlBtn_OnClick(object sender, RoutedEventArgs e)
        {
            UnpackMrnBtn.IsEnabled = false;
            XmlTreeView.Items.Clear();

            var openFileDialog = new OpenFileDialog();
            if (openFileDialog.ShowDialog() != true) return;

            FileLocationTxt1.Text = openFileDialog.FileName;
            if (FileLocationTxt1.Text.Trim() == string.Empty) return;
            Title = $"{_defaultTitle}::Loading XML file from \"{FileLocationTxt1.Text}\"";

            LoadXmlBtn.IsEnabled = false;

            _fragmentCollection.Clear();
            XmlFragments = await BuildXMLFragments(FileLocationTxt1.Text, _useRdmStrictMode)
                .ConfigureAwait(false);

            if (XmlFragments == null)
            {
                Title = $"{_defaultTitle}";
                LoadXmlBtn.IsEnabled = true;
                return;
            }

            //try
            {
                _messageList = await DecodeMrn(XmlFragments);
            }
            //catch (Exception ex)
            // {
            //     MessageBox.Show(ex.Message);
            // }


            Application.Current.Dispatcher.Invoke(() =>
            {
                XmlFragmentGrid1.ItemsSource = _fragmentCollection;
                Title = $"{_defaultTitle} Found {_fragmentCollection.Count} XML Fragments";
                LoadXmlBtn.IsEnabled = true;
                UpdateDataGridFilterComboboxItems();
            });
        }

        private void UpdateDataGridFilterComboboxItems()
        {
            DomainCheckBoxItems.Clear();
            MsgTypeCheckBoxItems.Clear();
            StreamIdCheckBoxItems.Clear();
            KeyNameCheckBoxItems.Clear();

            foreach (var item in (from x in _fragmentCollection select x.DomainType).Distinct())
                DomainCheckBoxItems.Add(new CheckBoxStringItem {IsChecked = true, Item = item});

            DomainComboBox.ItemsSource = DomainCheckBoxItems;

            foreach (var item in (from x in _fragmentCollection select x.RdmMessageType).Distinct())
                MsgTypeCheckBoxItems.Add(new CheckBoxStringItem {IsChecked = true, Item = item});

            MsgTypeComboBox.ItemsSource = MsgTypeCheckBoxItems;

            foreach (var item in (from x in _fragmentCollection select x.StreamId).Distinct())
                StreamIdCheckBoxItems.Add(new CheckBoxIntItem {IsChecked = true, Item = item});
            StreamIdComboBox.ItemsSource = StreamIdCheckBoxItems;
            KeyNameCheckBoxItems.Add(new CheckBoxStringItem {IsChecked = true, Item = string.Empty});
            foreach (var item in (from x in _fragmentCollection select x.RequestKeyName ?? string.Empty).Distinct())
                if (!string.IsNullOrEmpty(item))
                    KeyNameCheckBoxItems.Add(new CheckBoxStringItem {IsChecked = true, Item = item});
            KeyNameComboBox.ItemsSource = KeyNameCheckBoxItems;


            DomainComboBox.IsEnabled = true;
            StreamIdComboBox.IsEnabled = true;
            MsgTypeComboBox.IsEnabled = true;
            KeyNameComboBox.IsEnabled = true;
            if (_messageList.GetMrnGuidList().Any())
            {
                FilterStackPanel.IsEnabled = true;
                XmlFragmentGrid1.Columns[7].Visibility = Visibility.Visible;
                FilterStackPanel.Visibility = Visibility.Visible;
            }
            else
            {
                FilterStackPanel.IsEnabled = false;
                XmlFragmentGrid1.Columns[7].Visibility = Visibility.Hidden;
                FilterStackPanel.Visibility = Visibility.Hidden;
            }
        }

        private async Task<XmlFragmentList> BuildXMLFragments(string filePath, bool rdmStrictMode)
        {
            var result = await XMLTraceDecoder.LoadXmlFileAsync(filePath, rdmStrictMode).ConfigureAwait(false);

            if (!result.Item1)
            {
                MessageBox.Show($"Error:{result.Item3}");
                return result.Item2 ?? null;
            }

            return result.Item2;
        }

        private async Task<MrnMsgList> DecodeMrn(XmlFragmentList xmlFragments)
        {
            var messageList = new MrnMsgList();
            await Task.Run(async () =>
            {
                var decodeMrnResult = await MrnFragmentDecoder.DecodeMrnDataAsync(xmlFragments)
                    .ConfigureAwait(false);
                if (!decodeMrnResult.Item1)
                {
                    MessageBox.Show(decodeMrnResult.Item3);
                    return null;
                }

                messageList = decodeMrnResult.Item2;

                foreach (var data in xmlFragments.Fragments)
                {
                    var xmlFragment = new XMLFragmentsData
                    {
                        Index = data.FragmentNumber,
                        RdmMessageType = data.RdmMessageType,
                        TimeStamp = data.TimeStamp,
                        DomainType = messageList.Get(data.FragmentNumber).DomainType,
                        MsgDirection = data.MsgTypeRawXmlData != string.Empty
                            ? data.IsIncomingMsg ? "Incoming" : "Outgoing"
                            : string.Empty,
                        XmlRawData = data.RawXmlData,
                        GUID = string.Empty,
                        RequestKeyName = messageList.Get(data.FragmentNumber).RequestKeyName ?? string.Empty,
                        StreamId = int.Parse(messageList.Get(data.FragmentNumber).GetFragmentAttribs()["streamId"])
                    };

                    if (messageList.Get(data.FragmentNumber).ContainsFieldList)
                    {
                        if (messageList.Get(data.FragmentNumber).GetFieldList().ContainsKey(4271))
                            if (!string.IsNullOrEmpty(messageList.Get(data.FragmentNumber).GetFieldList()[4271].Trim()))
                            {
                                var pBuffer =
                                    TraceStringToString(
                                        messageList.Get(data.FragmentNumber).GetFieldList()[4271].Trim());
                                xmlFragment.GUID = pBuffer;
                            }

                        if (messageList.Get(data.FragmentNumber).GetFieldList().ContainsKey(32479))
                            if (!string.IsNullOrEmpty(messageList.Get(data.FragmentNumber).GetFieldList()[32479].Trim())
                            )
                            {
                                var mrnFragmentNumber =
                                    HexStringToInt(messageList.Get(data.FragmentNumber).GetFieldList()[32479].Trim()) ??
                                    0;
                                xmlFragment.MrnFragmentNumber = mrnFragmentNumber;
                            }
                    }

                    Dispatcher.Invoke(() => { _fragmentCollection.Add(xmlFragment); });
                }

                return messageList;
            });
            return messageList;
        }

        private async void XmlFragmentGrid1_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var xmlGridData = (XMLFragmentsData) XmlFragmentGrid1.SelectedItem;
            if (xmlGridData == null) return;
            _selectedIndex = xmlGridData.Index - 1;

            XmlTreeView.Items.Clear();

            //MessageBox.Show($"{_selectedIndex} {_fragmentCollection.Count}");
            if (_fragmentCollection.Count <= 0 || _selectedIndex < 0) return;

            if (!(_rdmFieldDictLoaded && _enumTypeDefDictLoaded))
            {
                Title = $"{_defaultTitle}::Loading RDMFieldDictioanary file";
                // Test Rdm dict
                var rdmResult = await RdmUtils.LoadRdmDictionaryAsync(_rdmDictFilePath);
                _fieldDictionary = rdmResult.Item2;
                if (!rdmResult.Item1)
                {
                    _rdmFieldDictLoaded = false;
                    MessageBox.Show(rdmResult.Item3);
                }

                Title = $"{_defaultTitle}::Loading enumtype.def file";
                var enumResult = await RdmUtils.LoadEnumTypeDefAsync(_enumTypeDefFilePath);
                _enumTypeDef = enumResult.Item2;
                if (!enumResult.Item1)
                {
                    _enumTypeDefDictLoaded = false;
                    MessageBox.Show(enumResult.Item3);
                }

                _rdmFieldDictLoaded = true;
                _enumTypeDefDictLoaded = true;
            }

            Title = $"{_defaultTitle}::Building TreeView from selected XML Fragment";
            var settings = new XmlReaderSettings
            {
                ConformanceLevel = ConformanceLevel.Fragment, Async = true, IgnoreWhitespace = true
            };

            using (var reader = XmlReader.Create(new StringReader(_fragmentCollection[_selectedIndex].XmlRawData),
                settings))
            {
                var tree = new TreeViewItem
                {
                    // Header =$"{_fragmentCollection[_selectedIndex].TimeStamp} {_fragmentCollection[_selectedIndex].MsgDirection}",
                    Header =
                        $"{XmlFragments.Get(_selectedIndex + 1).MsgTypeRawXmlData}\r\n{XmlFragments.Get(_selectedIndex + 1).TimeRawXmlData}\r\n{XmlFragments.Get(_selectedIndex + 1).RWFMajorMinorVersionRawXmlData}",
                    IsExpanded = true
                }; // instantiate TreeViewItem

                // assign name to TreeViewItem        
                XmlTreeView.Items.Add(tree); // add TreeViewItem to TreeView  
                await BuildTreeAsync(reader, tree).ConfigureAwait(false);
            }

            UnpackMrnBtn.IsEnabled = !string.IsNullOrEmpty(_fragmentCollection[_selectedIndex].GUID);
            UnpackMrnBtn.Visibility = UnpackMrnBtn.IsEnabled ? Visibility.Visible : Visibility.Collapsed;

            XmlTreeView.Visibility = Visibility.Visible;
            XmlTreeView.IsEnabled = true;

            Title = _defaultTitle;
        }


        private async Task BuildTreeAsync(XmlReader reader, TreeViewItem treeViewItem)
        {
            // TreeViewItem to add to existing tree
            var newNode = new TreeViewItem {IsExpanded = true};
            while (await reader.ReadAsync())
            {
                // build tree based on node type
                switch (reader.NodeType)
                {
                    // if Text node, add its value to tree
                    case XmlNodeType.Text:
                        newNode.Header = reader.Value;
                        treeViewItem.Items.Add(newNode);
                        break;
                    case XmlNodeType.EndElement: // if EndElement, move up tree
                        treeViewItem = (TreeViewItem) treeViewItem.Parent;
                        break;

                    // if new element, add name and traverse tree
                    case XmlNodeType.Element:
                    {
                        var elementType = reader.NodeType.ToString();
                        var elementName = reader.Name;

                        var attributeStr = string.Empty;
                        //To keep atrribute Name and its Value in Dictionary
                        if (reader.HasAttributes)
                        {
                            var count = reader.AttributeCount;
                            var fidId = 0;
                            for (var i = 0; i < count; i++)
                            {
                                reader.MoveToAttribute(i);
                                var attributeName = reader.Name.Trim();
                                var attributeVal = reader.Value.Trim();
                                var hexValue = "";
                                if (elementName == "fieldEntry")
                                    switch (attributeName)
                                    {
                                        case "fieldId":
                                        {
                                            fidId = int.Parse(attributeVal);
                                            if (_fieldDictionary.GetDictEntryById(fidId, out var rdmEntry))
                                                attributeVal = $"{rdmEntry.Acronym}({int.Parse(attributeVal)})";
                                            else attributeVal = $"Unknown({int.Parse(attributeVal)})";

                                            break;
                                        }
                                        case "data":
                                        {
                                            hexValue = attributeVal;
                                            if (XMLFragmentsData.FieldValueToString(
                                                fidId,
                                                attributeVal, _fieldDictionary, _enumTypeDef,
                                                out var output,
                                                out var errorMsg))
                                                attributeVal = $"{output}";

                                            break;
                                        }
                                    }

                                attributeStr += $" {attributeName}=\"{attributeVal}\"";
                                if (attributeName == "data")
                                {
                                    attributeStr += $" Hex=\"{hexValue}\"";
                                }
                            }
                        }

                        newNode.Header = $"{elementName} {attributeStr}";
                        reader.MoveToElement();

                        treeViewItem.Items.Add(newNode);

                        if (!reader.IsEmptyElement) treeViewItem = newNode;
                    }
                        break;

                    default:
                        newNode.Header = reader.NodeType.ToString();
                        treeViewItem.Items.Add(newNode);
                        break;
                }

                newNode = new TreeViewItem {IsExpanded = true};
            }
        }

        private async void UnpackMRNBtn_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                // Find fragment index from row that we click in Datagrid
                if (string.IsNullOrEmpty(_fragmentCollection[_selectedIndex].GUID.Trim())) return;
                var xmlGridData = (XMLFragmentsData) XmlFragmentGrid1.SelectedItem;
                Title = $"{_defaultTitle}::Searching XML Fragment contains GUID {xmlGridData.GUID}";

                // Use fragment index to get Guid value and get most of XML Fragments which contains the same guid,
                // we need some calculation to calculate start index from _messageList data structure. The position of Index user are selecting could be part of fragments.
                // The row that we choose in Datagrid may not be the first one which provide total size. 
                // Please note that there could be multiple fragments which use the same GUID so we need more complex logic to handle of this issue.

                var mrnMsgs = _messageList.GetMrnByGuid(xmlGridData.GUID);
                
                var startIndex = 0;
                if (xmlGridData.MrnFragmentNumber <= 1)
                    startIndex = xmlGridData.Index;
                else
                {
                    // Find the index from mrnMsgs which contains the same GUID and it must has MrnFragmentNumber equals to 1;
                    var currentIndex = mrnMsgs.FindIndex(x => x.FragmentNumber == (_selectedIndex+1));

                    // Calculate the startIndex from the currentIndex.
                    startIndex =
                        (int) mrnMsgs?.ElementAt<MrnMsg>(currentIndex - (xmlGridData.MrnFragmentNumber - 1))
                            .FragmentNumber;
                }

                var newIndex = mrnMsgs.FindIndex(x => x.FragmentNumber == startIndex);

                // Create New list which contains only all fragments for specific guid so we need to remove the duplicated ones.
                var newList = new List<MrnMsg>();
                var currentMrnFragment = 1;
                newList.Add(mrnMsgs.ElementAt(newIndex));

                while (++newIndex < mrnMsgs.Count)
                    if (mrnMsgs.ElementAt(newIndex).GetFieldList().ContainsKey(32479))
                    {
                        if (!string.IsNullOrEmpty(mrnMsgs.ElementAt(newIndex).GetFieldList()[32479].Trim()))
                            currentMrnFragment =
                                HexStringToInt(mrnMsgs.ElementAt(newIndex).GetFieldList()[32479].Trim()) ?? 0;

                        if (currentMrnFragment == 1) break;
                        newList.Add(mrnMsgs.ElementAt(newIndex));
                    }

                Title = $"{_defaultTitle}::Unpacking XML messages containing GUID {xmlGridData.GUID}";

                //Pass the list to MrnFragmentDecoder class and call method UnpackMrnData to unpack it.
                var result = await MrnFragmentDecoder.UnpackMrnDataAsync(newList);
                if (!result.Item1)
                {
                    MessageBox.Show($"Unpack Failed {result.Item3}");
                    return;
                }

                //After passing the steps we expected to receive Json data. Reformat the string and then shows it in Display Windows.
                //We will keep the raw Json data in JsonUnpackToken property so we can save the raw json data to file later.
                var displayWindows = new DisplayMRNData();
                displayWindows.JsonUnpackToken = JToken.Parse(result.Item2);
                displayWindows.RawData = $"{displayWindows.JsonUnpackToken.ToString(Formatting.Indented)}";
                displayWindows.RawData = displayWindows.RawData.Replace("\\n", "\r\n");
                displayWindows.RawData = displayWindows.RawData.Replace("\\t", "\t");
                displayWindows.RawHeaderText =
                    $"     Found {newList.Count} XML Fragments for GUID {xmlGridData.GUID}    ";

                displayWindows.XmlRawTreeView.Items.Clear();

                foreach (var fragment in newList)
                {
                    var settings = new XmlReaderSettings
                    {
                        ConformanceLevel = ConformanceLevel.Fragment, Async = true, IgnoreWhitespace = true
                    };
                    using (var reader = XmlReader.Create(
                        new StringReader($"{XmlFragments.Get(fragment.FragmentNumber).RawXmlData}"), settings))
                    {
                        var tree = new TreeViewItem
                        {
                            Header =
                                $"{XmlFragments.Get(fragment.FragmentNumber).TimeRawXmlData} {XmlFragments.Get(fragment.FragmentNumber).MsgTypeRawXmlData}",
                            IsExpanded = true
                        };


                        displayWindows.XmlRawTreeView.Items.Add(tree);
                        await BuildTreeAsync(reader, tree);
                    }
                }

                displayWindows.Title = $"DisplayMRNData:: GUID::{xmlGridData.GUID}";
                displayWindows.Show();
                Title = _defaultTitle;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Unpack compressed data to Json failed: {ex.Message}\r\nData might be corrupted or contains incomplete data");
            }
        }

        private void DomainComboBox_OnDropDownClosed(object sender, EventArgs e)
        {
            UpdateFilter();
        }

        private void KeyNameComboBox_OnDropDownClosed(object sender, EventArgs e)
        {
            UpdateFilter();
        }

        private void MsgTypeComboBox_OnDropDownClosed(object sender, EventArgs e)
        {
            UpdateFilter();
        }

        private void StreamIdComboBox_OnDropDownClosed(object sender, EventArgs e)
        {
            UpdateFilter();
        }

        private void SearchGuidBtn_OnClick(object sender, RoutedEventArgs e)
        {
            UpdateFilter();
            var viewItemSource = from item in XmlFragmentGrid1.Items.OfType<XMLFragmentsData>()
                where item.GUID == SearchGuidText.Text.Trim()
                select item;
            XmlFragmentGrid1.ItemsSource = viewItemSource;
        }

        private void UpdateFilter()
        {
            var domainList = new List<string>();
            foreach (var item in DomainComboBox.Items.OfType<CheckBoxStringItem>())
                if (item.IsChecked)
                    domainList.Add(item.Item.Trim());
            var msgTypeList = new List<string>();
            foreach (var item in MsgTypeComboBox.Items.OfType<CheckBoxStringItem>())
                if (item.IsChecked)
                    msgTypeList.Add(item.Item.Trim());
            var streamIdList = new List<int>();
            foreach (var item in StreamIdComboBox.Items.OfType<CheckBoxIntItem>())
                if (item.IsChecked)
                    streamIdList.Add(item.Item);
            var keyNameList = new List<string>();
            foreach (var item in KeyNameComboBox.Items.OfType<CheckBoxStringItem>())
                if (item.IsChecked)
                    keyNameList.Add(item.Item);


            var viewItemSource = from x in _fragmentCollection
                where domainList.Contains(x.DomainType.Trim()) && msgTypeList.Contains(x.RdmMessageType.Trim()) &&
                      streamIdList.Contains(x.StreamId) && keyNameList.Contains(x.RequestKeyName.Trim())
                select x;

            XmlFragmentGrid1.ItemsSource = viewItemSource;
            UnpackMrnBtn.IsEnabled = false;
        }

        private void SearchGuidText_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            // Blank mean reset
            if (string.IsNullOrEmpty(SearchGuidText.Text.Trim()))
            {
                SearchGuidBtn.IsEnabled = false;
                UpdateFilter();
            }
            else
            {
                SearchGuidBtn.IsEnabled = true;
            }

            UnpackMrnBtn.IsEnabled = false;
        }

        private void XmlTreeView_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var node = XmlTreeView.SelectedItem;
            if (node != null) Clipboard.SetDataObject(((TreeViewItem) node).Header, true);
        }
    }
}