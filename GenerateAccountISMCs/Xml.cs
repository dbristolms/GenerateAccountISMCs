using System;
using System.Text;
using System.Xml;

namespace GenerateAccountISMCs
{
    public class Xml
    {
        public static string AddIsmcToIsm(string ismXmlContent, string newIsmcFileName)
        {
            // Example head tag for the ISM on how to include the ISMC.
            // <head>
            //   < meta name = "clientManifestRelativePath" content = "GOPR0881.ismc" />
            //   < meta name = "formats" content = "mp4-v3" />
            // </ head >

            byte[] array = Encoding.ASCII.GetBytes(ismXmlContent);
            // Checking and removing Byte Order Mark (BOM) for UTF-8 if present.
            if (array[0] == 63)
            {
                byte[] tempArray = new byte[array.Length - 1];
                Array.Copy(array, 1, tempArray, 0, tempArray.Length);
                ismXmlContent = Encoding.UTF8.GetString(tempArray);
            }

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(ismXmlContent);
            // If the node is already there we should skip this asset.  Maybe.  Or maybe update it?
            if (doc != null && ismXmlContent.IndexOf("clientManifestRelativePath") < 0)
            {
                XmlNodeList node = doc.GetElementsByTagName("head"); // Does this always exist?  If not, we may need to create it.

                XmlElement newClientManifest = doc.CreateElement("meta");
                XmlAttribute name = doc.CreateAttribute("name");
                newClientManifest.Attributes.Append(name);
                name.Value = "clientManifestRelativePath";
                XmlAttribute content = doc.CreateAttribute("content");
                content.Value = newIsmcFileName;
                newClientManifest.Attributes.Append(content);
                node[0].AppendChild(newClientManifest);
            }
            return doc.OuterXml;
        }

        public static string RemoveXmlNode(string ismcContentXml)
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(ismcContentXml);
            XmlNode node = doc.SelectSingleNode("//SmoothStreamingMedia");
            XmlNode child = doc.SelectSingleNode("//Protection");
            node.RemoveChild(child);
            return doc.OuterXml;
        }
    }
}
