using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Http;
using Our.Umbraco.Vorto.Web.PropertyEditors;
using umbraco;
using Umbraco.Core;
using Umbraco.Core.Models;
using Umbraco.Core.PropertyEditors;
using Umbraco.Web.Editors;
using Umbraco.Web.Mvc;
using Language = Our.Umbraco.Vorto.Models.Language;

namespace Our.Umbraco.Vorto.Web.Controllers
{
	[PluginController("VortoApi")]
	public class VortoApiController : UmbracoAuthorizedJsonController
	{
		public IEnumerable<object> GetNonVortoDataTypes()
		{
			return Services.DataTypeService.GetAllDataTypeDefinitions()
				.Where(x => x.PropertyEditorAlias != VortoPropertyEditor.PropertyEditorAlias)
				.OrderBy(x => x.SortOrder)
				.Select(x => new
				{
					guid = x.Key,
					name = x.Name,
					propertyEditorAlias = x.PropertyEditorAlias
				});
		}

		public object GetDataTypeById(Guid id)
		{
			var dtd = Services.DataTypeService.GetDataTypeDefinitionById(id);
			return FormatDataType(dtd);
		}

		public object GetDataTypeByAlias(string contentType, string contentTypeAlias, string propertyAlias)
		{
            IContentTypeComposition ct = null;
            
		    switch (contentType)
            {
                case "member":
                    ct = Services.MemberTypeService.Get(contentTypeAlias);
                    break;
                case "content":
                    ct = Services.ContentTypeService.GetContentType(contentTypeAlias);
		            break;
                case "media":
                    ct = Services.ContentTypeService.GetMediaType(contentTypeAlias);
		            break;
		    }

		    var prop = ct?.CompositionPropertyTypes.SingleOrDefault(x => x.Alias == propertyAlias);
			if (prop == null)
				return null;

			var dtd = Services.DataTypeService.GetDataTypeDefinitionById(prop.DataTypeDefinitionId);
			return FormatDataType(dtd);
		}

		protected object FormatDataType(IDataTypeDefinition dtd)
		{
			if (dtd == null)
				throw new HttpResponseException(HttpStatusCode.NotFound);

			var propEditor = PropertyEditorResolver.Current.GetByAlias(dtd.PropertyEditorAlias);

			// Force converter before passing prevalues to view
			var preValues = Services.DataTypeService.GetPreValuesCollectionByDataTypeId(dtd.Id);
			var convertedPreValues = propEditor.PreValueEditor.ConvertDbToEditor(propEditor.DefaultPreValues,
				preValues);

			return new
			{
				guid = dtd.Key,
				propertyEditorAlias = dtd.PropertyEditorAlias,
				preValues = convertedPreValues,
				view = propEditor.ValueEditor.View
			};
		}

        public IEnumerable<object> GetLanguages(string section, int id, int parentId, Guid dtdGuid)
		{
            var dtd = Services.DataTypeService.GetDataTypeDefinitionById(dtdGuid);
		    if (dtd == null) return Enumerable.Empty<object>();

			var preValues = Services.DataTypeService.GetPreValuesCollectionByDataTypeId(dtd.Id).PreValuesAsDictionary;
			var languageSource = preValues.ContainsKey("languageSource") ? preValues["languageSource"].Value : "";
			var primaryLanguage = preValues.ContainsKey("primaryLanguage") ? preValues["primaryLanguage"].Value : "";
            var content = Services.ContentService.GetById(id);
			var languages = new List<Language>();

			if (languageSource == "inuse")
			{
                var currentNode = id != 0 ? Umbraco.TypedContent(id) : null;
                var currentNodeIsUnpublished = currentNode == null;
                var parentOrSelfId = currentNodeIsUnpublished ? parentId : id;

                // trying to add/publish a home node, so no "in use" languages have been defined/are accessible - display all installed in the interim
                var currentNodeIsUnpublishedRootNode = currentNodeIsUnpublished && parentId == -1;

                var xpath = preValues.ContainsKey("xpath") ? preValues["xpath"].Value : "";

                // Grab languages by xpath (only if in content section)
                if (!currentNodeIsUnpublishedRootNode && !string.IsNullOrWhiteSpace(xpath) && section == "content")
                {
                    xpath = xpath.Replace("$currentPage",
                        $"//*[@id={id} and @isDoc]").Replace("$parentPage",
                            $"//*[@id={parentId} and @isDoc]").Replace("$ancestorOrSelf",
                               $"//*[@id={parentOrSelfId} and @isDoc]");

                    var ancestorNodeAlias = string.Empty;
                    var xpathSplit = xpath.Split('/');
                    var propAlias = xpathSplit.LastOrDefault();

                    if (!string.IsNullOrWhiteSpace(propAlias))
                    {
                        var xpathSectionsWithoutPropAlias = xpathSplit.Where(x => x != propAlias).ToList();

                        if (xpathSectionsWithoutPropAlias.Any())
                        {
                            var lastSection = xpathSectionsWithoutPropAlias.LastOrDefault();
                            ancestorNodeAlias = lastSection != null ? lastSection.Substring(lastSection.LastIndexOf(':') + 1) : string.Empty;
                        }

                        if (!string.IsNullOrWhiteSpace(ancestorNodeAlias))
                        {
                            var ancestor = Services.ContentService.GetAncestors(id).FirstOrDefault(x => x.ContentType.Alias == ancestorNodeAlias);

                            if (ancestor != null && ancestor.HasProperty(propAlias))
                            {
                                var replacements = new Dictionary<string, string>() { { "[", string.Empty }, { "]", string.Empty }, { "\"", string.Empty } };
                                var propVal = ancestor.GetValue<string>(propAlias);
                                var langs = Regex.Replace(propVal.ReplaceMany(replacements), "\\s+", string.Empty).Split(',');

                                if (langs.Any())
                                {
                                    languages.AddRange(langs.Select(CultureInfo.GetCultureInfo).Select(x => new Language
                                    {
                                        IsoCode = x.Name,
                                        Name = x.DisplayName,
                                        NativeName = x.NativeName
                                    }));
                                }
                            }
                        }
                    }

                    if (!languages.Any())
                    {
                        // Lookup language nodes
                        var nodeIds = uQuery.GetNodesByXPath(xpath).Select(x => x.Id).ToArray();
                        if (nodeIds.Any())
                        {
                            var db = ApplicationContext.Current.DatabaseContext.Database;
                            languages.AddRange(db.Query<string>(
                                    string.Format(
                                        "SELECT DISTINCT [languageISOCode] FROM [umbracoLanguage] JOIN [umbracoDomains] ON [umbracoDomains].[domainDefaultLanguage] = [umbracoLanguage].[id] WHERE [umbracoDomains].[domainRootStructureID] in ({0})",
                                        string.Join(",", nodeIds)))
                                .Select(CultureInfo.GetCultureInfo)
                                .Select(x => new Language
                                {
                                    IsoCode = x.Name,
                                    Name = x.DisplayName,
                                    NativeName = x.NativeName,
                                    IsRightToLeft = x.TextInfo.IsRightToLeft
                                }));
                        }
                    }
                }
                else
				{
					// No language node xpath so just return a list of all languages in use
					var db = ApplicationContext.Current.DatabaseContext.Database;
					languages.AddRange(
						db.Query<string>(
							"SELECT [languageISOCode] FROM [umbracoLanguage] WHERE EXISTS(SELECT 1 FROM [umbracoDomains] WHERE [umbracoDomains].[domainDefaultLanguage] = [umbracoLanguage].[id])")
							.Select(CultureInfo.GetCultureInfo)
							.Select(x => new Language
							{
								IsoCode = x.Name,
								Name = x.DisplayName,
                                NativeName = x.NativeName,
                                IsRightToLeft = x.TextInfo.IsRightToLeft
							}));
				}
			}
			else
			{
				languages.AddRange(umbraco.cms.businesslogic.language.Language.GetAllAsList()
					.Select(x => CultureInfo.GetCultureInfo(x.CultureAlias))
					.Select(x => new Language
					{
						IsoCode = x.Name,
						Name = x.DisplayName,
                        NativeName = x.NativeName,
                        IsRightToLeft = x.TextInfo.IsRightToLeft
					}));
			}

			// Raise event to allow for further filtering
			var args = new FilterLanguagesEventArgs
			{
				CurrentPageId = id,
				ParentPageId = parentId,
				Languages = languages
			};

			Vorto.CallFilterLanguages(args);

			// Set active language
			var currentCulture = Thread.CurrentThread.CurrentUICulture.Name;

			// See if one has already been set via the event handler
			var activeLanguage = args.Languages.FirstOrDefault(x => x.IsDefault);

			// Try setting to primary language
			if (activeLanguage == null && !string.IsNullOrEmpty(primaryLanguage))
				activeLanguage = args.Languages.FirstOrDefault(x => x.IsoCode == primaryLanguage);

			// Try settings to exact match of current culture
			if (activeLanguage == null)
				activeLanguage = args.Languages.FirstOrDefault(x => x.IsoCode == currentCulture);

			// Try setting to nearest match
			if (activeLanguage == null)
				activeLanguage = args.Languages.FirstOrDefault(x => x.IsoCode.Contains(currentCulture));

			// Try setting to nearest match
			if (activeLanguage == null)
				activeLanguage = args.Languages.FirstOrDefault(x => currentCulture.Contains(x.IsoCode));

			// Couldn't find a good enough match, just select the first language
			if (activeLanguage == null)
				activeLanguage = args.Languages.FirstOrDefault();

			if (activeLanguage != null)
				activeLanguage.IsDefault = true;

			// Return results
			return args.Languages;
		}

		public IEnumerable<object> GetInstalledLanguages()
		{
			var languages = new List<Language>();

			languages.AddRange(umbraco.cms.businesslogic.language.Language.GetAllAsList()
				.Select(x => CultureInfo.GetCultureInfo(x.CultureAlias))
				.Select(x => new Language
				{
					IsoCode = x.Name,
					Name = x.DisplayName,
                    NativeName = x.NativeName,
                    IsRightToLeft = x.TextInfo.IsRightToLeft
				}));

			return languages;
		}
	}
}
