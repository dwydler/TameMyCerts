﻿// Copyright 2021-2025 Uwe Gradenegger <info@gradenegger.eu>

// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at

// http://www.apache.org/licenses/LICENSE-2.0

// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.DirectoryServices.ActiveDirectory;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using TameMyCerts.Enums;

namespace TameMyCerts.Models;

internal class ActiveDirectoryObject
{
    private const StringComparison Comparison = StringComparison.InvariantCultureIgnoreCase;
    private static readonly TimeSpan LdapClientTimeout = new(0, 0, 15);

    public ActiveDirectoryObject(string forestRootDomain, string dsAttribute, string identity,
        string objectCategory, string searchRoot, bool resolveNestedGroupMemberships = false)
    {
        if (!DsMappingAttributes.Any(s => s.Equals(dsAttribute, Comparison)))
        {
            throw new ArgumentException(string.Format(LocalizedStrings.DirVal_Invalid_Directory_Attribute,
                dsAttribute));
        }

        if (!DsObjectTypes.Any(s => s.Equals(objectCategory, Comparison)))
        {
            throw new ArgumentException(string.Format(LocalizedStrings.DirVal_Invalid_Object_Category,
                objectCategory));
        }

        // Automatically determine the searchRoot from the global catalog
        if (string.IsNullOrEmpty(searchRoot))
        {
            var searchResult = GetDirectoryEntry($"GC://{forestRootDomain}", dsAttribute, identity, objectCategory,
                ["distinguishedName"]);
            searchRoot = (string)searchResult.Properties["distinguishedName"][0];
        }

        var attributesToRetrieve = new List<string>
        {
            "memberOf", "userAccountControl", "objectSid", "distinguishedName", "servicePrincipalName"
        };

        attributesToRetrieve.AddRange(DsRetrievalAttributes);

        var dsObject = GetDirectoryEntry($"LDAP://{searchRoot}", dsAttribute, identity, objectCategory,
            attributesToRetrieve);

        UserAccountControl = (UserAccountControl)Convert.ToInt32(dsObject.Properties["userAccountControl"][0]);
        SecurityIdentifier = new SecurityIdentifier((byte[])dsObject.Properties["objectSid"][0], 0);
        DistinguishedName = (string)dsObject.Properties["distinguishedName"][0];

        if (resolveNestedGroupMemberships)
        {
            var directorySearcher = new DirectorySearcher
            {
                SearchRoot = new DirectoryEntry($"LDAP://{DistinguishedName}"),
                PropertiesToLoad = { "msds-TokenGroupNames" },
                ClientTimeout = LdapClientTimeout,
                SearchScope = SearchScope.Base
            };

            var tokenGroupNames = directorySearcher.FindOne();

            if (tokenGroupNames == null || tokenGroupNames.Properties["msds-TokenGroupNames"].Count == 0)
            {
                // msds-TokenGroupNames is only available on Windows 2016 or newer DCs
                // Querying this attribute against an older OS DC will return an empty result.
                // We throw an exception and therefore cause the request being denied in that case.
                throw new Exception(string.Format(LocalizedStrings.DirVal_TokenGroupNames_Failed, DistinguishedName));
            }

            for (var index = 0; index < tokenGroupNames.Properties["msds-TokenGroupNames"].Count; index++)
            {
                MemberOf.Add(tokenGroupNames.Properties["msds-TokenGroupNames"][index].ToString());
            }
        }
        else
        {
            for (var index = 0; index < dsObject.Properties["memberOf"].Count; index++)
            {
                MemberOf.Add(dsObject.Properties["memberOf"][index].ToString());
            }
        }

        for (var index = 0; index < dsObject.Properties["servicePrincipalName"].Count; index++)
        {
            ServicePrincipalNames.Add(dsObject.Properties["servicePrincipalName"][index].ToString());
        }

        foreach (var s in DsRetrievalAttributes.Where(s => dsObject.Properties[s].Count > 0))
        {
            if (dsObject.Properties[s][0] is long)
            {
                Attributes.Add(s, dsObject.Properties[s][0].ToString());
            }
            else
            {
                Attributes.Add(s, (string)dsObject.Properties[s][0]);
            }
        }
    }

    // To inject Unit tests
    public ActiveDirectoryObject(string distinguishedName, UserAccountControl userAccountControl,
        List<string> memberOf, Dictionary<string, string> attributes, SecurityIdentifier securityIdentifier,
        List<string> servicePrincipalNames)
    {
        DistinguishedName = distinguishedName;
        UserAccountControl = userAccountControl;
        MemberOf = memberOf;
        Attributes = attributes;
        SecurityIdentifier = securityIdentifier;
        ServicePrincipalNames = servicePrincipalNames;
    }

    public string DistinguishedName { get; }

    public UserAccountControl UserAccountControl { get; }

    public List<string> MemberOf { get; } = new();

    public List<string> ServicePrincipalNames { get; } = new();

    public Dictionary<string, string> Attributes { get; } = new(StringComparer.InvariantCultureIgnoreCase);

    public SecurityIdentifier SecurityIdentifier { get; }

    private static IEnumerable<string> DsMappingAttributes { get; } = new List<string>
        { "cn", "name", "sAMAccountName", "userPrincipalName", "dNSHostName" };

    private static IEnumerable<string> DsObjectTypes { get; } = new List<string> { "computer", "user" };

    private static List<string> DsRetrievalAttributes { get; } = new()
    {
        "c", "co", "cn", "company", "department", "departmentNumber", "description", "displayName", "division",
        "dNSHostName", "employeeID", "employeeNumber", "employeeType", "extensionAttribute1",
        "extensionAttribute10", "extensionAttribute11", "extensionAttribute12", "extensionAttribute13",
        "extensionAttribute14", "extensionAttribute15", "extensionAttribute2", "extensionAttribute3",
        "extensionAttribute4", "extensionAttribute5", "extensionAttribute6", "extensionAttribute7",
        "extensionAttribute8", "extensionAttribute9", "facsimileTelephoneNumber", "gecos", "givenName", "homePhone",
        "homePostalAddress", "info", "initials", "l", "location", "mail", "mailNickname", "middleName", "mobile",
        "name", "otherMailbox", "otherMobile", "otherPager", "otherTelephone", "pager", "personalPager",
        "personalTitle", "postalAddress", "postalCode", "postOfficeBox", "pwdLastSet", "sAMAccountName", "sn", "st",
        "street",
        "streetAddress", "telephoneNumber", "title", "userPrincipalName"
    };

    private static SearchResult GetDirectoryEntry(string searchRoot, string dsAttribute, string identity,
        string objectCategory, List<string> searchProperties)
    {
        var filter = $"(&({dsAttribute}={EscapeForLdapSearchFilter(identity)})(objectCategory={objectCategory}))";
        SearchResultCollection searchResults;

        var directorySearcher = new DirectorySearcher
        {
            SearchRoot = new DirectoryEntry(searchRoot),
            Filter = filter,
            ClientTimeout = LdapClientTimeout,
            SearchScope = SearchScope.Subtree
        };

        foreach (var s in searchProperties)
        {
            directorySearcher.PropertiesToLoad.Add(s);
        }

        try
        {
            searchResults = directorySearcher.FindAll();
        }
        catch (Exception ex)
        {
            throw new ArgumentException(string.Format(LocalizedStrings.DirVal_Query_Failed,
                filter, searchRoot,
                ex is COMException ? $"0x{ex.HResult:X} ({ex.HResult}): {ex.Message}" : ex.Message));
        }

        // Calling Dispose is required to prevent SearchResultCollection leaking memory
        switch (searchResults.Count)
        {
            case < 1:
                searchResults.Dispose();
                throw new ActiveDirectoryObjectNotFoundException(string.Format(LocalizedStrings.DirVal_Nothing_Found,
                    objectCategory, dsAttribute, identity, searchRoot));
            case > 1:
                searchResults.Dispose();
                throw new ArgumentException(string.Format(LocalizedStrings.DirVal_Invalid_Result_Count, objectCategory,
                    dsAttribute, identity));
            default:
                var result = searchResults[0];
                searchResults.Dispose();
                return result;
        }
    }

    /// <summary>
    ///     Escapes the LDAP search filter to prevent LDAP injection attacks.
    /// </summary>
    /// <param name="input">The search filter.</param>
    /// <see cref="https://blogs.oracle.com/shankar/entry/what_is_ldap_injection" />
    /// <see cref="http://msdn.microsoft.com/en-us/library/aa746475.aspx" />
    /// <see cref="https://stackoverflow.com/questions/649149/how-to-escape-a-string-in-c-for-use-in-an-ldap-query" />
    /// <returns>The escaped search filter.</returns>
    private static string EscapeForLdapSearchFilter(string input)
    {
        var output = new StringBuilder();

        foreach (var character in input)
        {
            switch (character)
            {
                case '\\':
                    output.Append(@"\5c");
                    break;
                case '*':
                    output.Append(@"\2a");
                    break;
                case '(':
                    output.Append(@"\28");
                    break;
                case ')':
                    output.Append(@"\29");
                    break;
                case '\u0000':
                    output.Append(@"\00");
                    break;
                case '/':
                    output.Append(@"\2f");
                    break;
                default:
                    output.Append(character);
                    break;
            }
        }

        return output.ToString();
    }
}