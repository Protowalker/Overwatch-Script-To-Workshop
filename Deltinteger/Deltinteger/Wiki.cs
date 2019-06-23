using System;
using System.Net;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using HtmlAgilityPack;

namespace Deltin.Deltinteger.WorkshopWiki
{
    public class Wiki
    {
        public const string URL = "https://us.forums.blizzard.com/en/overwatch/t/wiki-workshop-syntax-script-database/";
        private static Log Log = new Log("Wiki");

        private static WikiMethod[] wiki = null; 
        private static void GetWiki()
        {
            if (wiki != null)
                return;
            
            HtmlDocument htmlDoc = new HtmlDocument();
            htmlDoc.OptionFixNestedTags = true;
        
            using (var webClient = new WebClient())
                htmlDoc.Load(webClient.OpenRead(URL), Encoding.UTF8);
            
            List<WikiMethod> methods = new List<WikiMethod>();

            // Loop through all summaries
            foreach(var summary in htmlDoc.DocumentNode.Descendants("summary"))
            {
                string name = summary.InnerText.Trim(); // Get sthe name

                var details = summary.ParentNode;
                string description = details.SelectNodes("p").First().InnerText.Trim(); // Gets the description.

                // Get the parameters.
                List<WikiParameter> parameters = new List<WikiParameter>();
                var parameterSummaries = details.SelectSingleNode("ul")?.SelectNodes("li"); // 'ul' being list and 'li' being list element.
                if (parameterSummaries != null)
                    foreach (var parameterSummary in parameterSummaries)
                    {
                        string[] data = parameterSummary.InnerText.Split(new char[]{'-'}, 2);
                        parameters.Add(new WikiParameter(data[0].Trim(), data.ElementAtOrDefault(1)?.Trim()));
                    }

                methods.Add(new WikiMethod(name, description, parameters.ToArray()));
            }
            wiki = methods.ToArray();
        }

        public static WikiMethod GetWikiMethod(string name)
        {
            GetWiki();
            var method = wiki.FirstOrDefault(w => w.Name.ToLower() == name.ToLower());
            return method;
        }
    }

    public class WikiMethod
    {
        public string Name;
        public string Description;
        public WikiParameter[] Parameters;
        public WikiMethod(string name, string description, WikiParameter[] parameters)
        {
            Name = name;
            Description = description;
            Parameters = parameters;
        }

        public override string ToString()
        {
            return Name;
        }

        public WikiParameter GetWikiParameter(string name)
        {
            return Parameters.FirstOrDefault(p => p.Name.ToLower() == name.ToLower());
        }
    }

    public class WikiParameter
    {
        public string Name;
        public string Description;
        public WikiParameter(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public override string ToString()
        {
            return Name;
        }
    }
}