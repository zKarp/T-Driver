﻿using System;
using System.Text.RegularExpressions;

namespace TDriver {
    /// <summary>
    ///     Determines the AP Document type and creates the corresponding object.
    /// </summary>
    /// <remarks>
    ///     Valid File Names:
    ///     Acct-99999-99999-To-999-999-9999-To-NAME_HERE.doc                       (Medical Fax Document)
    ///     Acct-99999-99999-To-1-999-999-9999-To-NAME_HERE.doc                     (Medical Fax Document) *when US-code added.
    ///     Acct-99999-99999.doc                                                    (Medical Anomaly Document) *still faxed
    ///     Acct-99999-99999-Doctor-To-999-999-9999-To-NAME_HERE.doc                (Medical Fax Document Doctor) *added 01/28/16
    ///     Acct-99999-99999-Customer-To-999-999-9999-To-NAME_HERE.doc                (Medical Fax Document Doctor) *added 01/28/16
    ///     DPA-99999-99999.doc                                                     (DPA Email, DPA Mail or DPA Fax Document)
    ///     DPA-99999-99999-For-NAME_HERE-FaxTo-1-999-999-9999-ATTN-NAME_HERE.doc   (DPA Fax Document)
    ///     DPA-99999-99999-For-NAME_HERE-FaxTo-1-999-999-9999                      (DPA Fax Document)
    ///     99999-99999_faxto-1-999-999-9999.doc                                    (NR DPA Fax Document)
    ///     99999-99999.doc                                                         (NR DPA Fax Document)
    /// </remarks>
    public static class AP_Factory {
        private static readonly Parser parser = new Parser();

        public static AP_Document Create(string file, AP_Subsection subsection) {
            //Determine the document type by checking against the general naming conventions
            DocumentType docType = DetermineDocumentType(file);
            if (docType == DocumentType.ERROR) {
                Logger.AddError(Settings.ErrorLogfile, file + " was skipped. Does NOT follow general naming convention.");
                return null;
            }

            //Open document with RTF Parser
            AP_Document newApDoc = parser.FindData(file, docType);
            if (newApDoc == null) {
                Logger.AddError(Settings.ErrorLogfile, file + " was skipped by the parser. Verify the formatting.");
                return null;
            }

            //Manage derived class, specific options
            //**DPA***
            if ((docType == DocumentType.DPA || docType == DocumentType.NR_DPA) && newApDoc is DPA) {
                //Add the type of DPA to the class.
                ((DPA)newApDoc).KindOfDPA = subsection.Name;
            }
            //**CME***
            if (docType == DocumentType.CME && newApDoc is MedicalCME) {
                //Verify the zip code to the specified region.
                //TODO Add region in settings.
                if (!((MedicalCME) newApDoc).CheckRegion("UPSTATE")) {
                    //When the region doesn't match the specified.
                    //Log it and mark as invalid.
                    Logger.AddError(Settings.ErrorLogfile, file + " is NOT an " + "UPSTATE" + " zip code. File skipped."  );
                    return null;
                }

            }

            //Determine the delivery method by analyzing the "SendTo" field's contents.
            Tuple<DeliveryMethodType, string> result = DetermineDeliveryMethod(newApDoc.SendTo);
            if (result == null) {
                Logger.AddError(Settings.ErrorLogfile, file + " was skipped. Couldn't determine who it's intended for.");
                return null;
            }

            //Change the delivery method and update where it's going to.
            newApDoc.ChangeDeliveryType(result.Item1);
            newApDoc.SendTo = result.Item2;
            //Return the new AP_Document
            return newApDoc;
        }

        /// <summary>
        ///     Find the delivery method for the document
        /// </summary>
        /// <param name="sendToField"></param>
        /// <returns>Tuple(DeliveryMethod, Recipient)</returns>
        private static Tuple<DeliveryMethodType, string> DetermineDeliveryMethod(string sendToField) {
            //Check for email address.
            const String rgxEmailPattern =
                @"[a-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[a-z0-9!#$%&'*+/=?^_`{|}~-]+)*@(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]*[a-z0-9])?";
            var rgx = new Regex(rgxEmailPattern, RegexOptions.IgnoreCase);
            Match emailMatch = rgx.Match(sendToField);
            if (emailMatch.Success) {
                return Tuple.Create(DeliveryMethodType.Email, emailMatch.Value);
            }

            //Check for a mailing address.
            //Look for a space, two letters, space and then 5 digits
            //ex. ' NY 13219'
            const String rgxAddressPattern = @"\s[a-z]{2}\s\d{5}";
            rgx = new Regex(rgxAddressPattern, RegexOptions.IgnoreCase);
            Match mailMatch = rgx.Match(sendToField);
            if (mailMatch.Success) {
                return Tuple.Create(DeliveryMethodType.Mail, sendToField);
            }

            //Check for VALID fax number
            // Return # found plus US-Code (1)
            const String rgxFaxPattern = @"\d{3}-\d{3}-\d{4}";
            rgx = new Regex(rgxFaxPattern, RegexOptions.IgnoreCase);
            Match faxMatch = rgx.Match(sendToField);
            if (faxMatch.Success && ValidateFaxNumber(faxMatch.Value)) {
                return Tuple.Create(DeliveryMethodType.Fax, "1-" + faxMatch.Value);
            }

            return null;
        }

        /// <summary>
        /// Determine the document type based on naming convention.
        /// </summary>
        /// <param name="file"></param>
        /// <returns></returns>
        private static DocumentType DetermineDocumentType(string file) {
            //Check for Medical CME
            //Looks for 'Acct-99999-99999'
            const String rgxCMEConvention = @"Acct-\d{5}-\d{5}";
            var rgx = new Regex(rgxCMEConvention, RegexOptions.IgnoreCase);
            Match cmeMatch = rgx.Match(file);
            if (cmeMatch.Success) {
                return DocumentType.CME;
            }

            //Check for DPA
            //Looks for 'DPA-99999-99999'
            const String rgxDPAConvention = @"DPA-\d{5}-\d{5}";
            rgx = new Regex(rgxDPAConvention, RegexOptions.IgnoreCase);
            Match dpaMatch = rgx.Match(file);
            if (dpaMatch.Success) {
                return DocumentType.DPA;
            }

            //Check for NR DPA
            //Looks for '99999-99999'
            //TODO find better way of determining if it's an NR DPA
            const String rgxNRDPAConvention = @"\d{5}-\d{5}";
            rgx = new Regex(rgxNRDPAConvention, RegexOptions.IgnoreCase);
            Match nrDPAMatch = rgx.Match(file);
            if (nrDPAMatch.Success) {
                return DocumentType.NR_DPA;
            }

            return DocumentType.ERROR;
        }

        /// <summary>
        ///     Validate the fax number.
        /// </summary>
        /// <remarks>
        ///     999-999-9999 and 888-888-8888 are common numbers used when incorrect form is used.
        /// </remarks>
        /// <returns>
        /// True: Valid fax number
        /// False: Invalid fax number</returns>
        private static Boolean ValidateFaxNumber(string faxNumber) {
            if ((faxNumber == "999-999-9999") || (faxNumber == "888-888-8888") || (faxNumber == "000-000-0000")) {
                return false;
            }
            return true;
        }

    }
}