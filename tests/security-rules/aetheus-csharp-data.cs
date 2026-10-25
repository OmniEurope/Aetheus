// SPDX-License-Identifier: EUPL-1.2
// Test corpus for .aetheus/security-rules/opengrep/aetheus-csharp-data.yml. Not compiled.
public class DataRuleCorpus : ControllerBase
{
    public void Formatters(Stream payload)
    {
        // ruleid: aetheus.csharp.unsafe-formatter
        var contract = new NetDataContractSerializer();
        // ruleid: aetheus.csharp.unsafe-formatter
        var soap = new SoapFormatter();
        // ruleid: aetheus.csharp.unsafe-formatter
        var serializer = new JavaScriptSerializer(new SimpleTypeResolver());
        // ok: aetheus.csharp.unsafe-formatter
        var model = JsonSerializer.Deserialize<Model>(payload);
        // ok: aetheus.csharp.unsafe-formatter
        var plain = new JavaScriptSerializer();
    }

    public void Settings()
    {
        // ruleid: aetheus.csharp.newtonsoft-type-name-handling
        var one = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };
        var two = new JsonSerializerSettings();
        // ruleid: aetheus.csharp.newtonsoft-type-name-handling
        two.TypeNameHandling = TypeNameHandling.Auto;
        // ok: aetheus.csharp.newtonsoft-type-name-handling
        var three = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None };
    }

    public void Entities()
    {
        // ruleid: aetheus.csharp.xxe-dtd-parse
        var risky = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse };
        // ok: aetheus.csharp.xxe-dtd-parse
        var safe = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        // ok: aetheus.csharp.xxe-dtd-parse
        var tolerant = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
    }

    public void Resolvers(XmlDocument document)
    {
        // ruleid: aetheus.csharp.xxe-url-resolver
        document.XmlResolver = new XmlUrlResolver();
        // ok: aetheus.csharp.xxe-url-resolver
        document.XmlResolver = null;
    }

    public XmlReader TextReader(Stream input)
    {
        // ruleid: aetheus.csharp.xxe-xml-text-reader
        return new XmlTextReader(input);
    }

    public XmlReader TextReaderProhibited(Stream input)
    {
        // ok: aetheus.csharp.xxe-xml-text-reader
        var reader = new XmlTextReader(input);
        reader.DtdProcessing = DtdProcessing.Prohibit;
        return reader;
    }

    public void Transforms(XslCompiledTransform transform)
    {
        // ruleid: aetheus.csharp.xslt-script-enabled
        transform.Load("sheet.xsl", new XsltSettings(true, true), null);
        // ruleid: aetheus.csharp.xslt-script-enabled
        var trusted = XsltSettings.TrustedXslt;
        // ok: aetheus.csharp.xslt-script-enabled
        var plain = XsltSettings.Default;
    }

    public IActionResult Create([FromQuery] string typeName)
    {
        // ruleid: aetheus.csharp.reflection-request-input
        var type = Type.GetType(typeName);
        return Ok(type);
    }

    public IActionResult CreateMapped([FromQuery] string typeName)
    {
        // ok: aetheus.csharp.reflection-request-input
        var type = Type.GetType("Example.Known.Report");
        return Ok(type);
    }
}
