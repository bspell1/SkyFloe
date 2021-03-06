﻿//===========================================================================
// MODULE:  TestCrcFilter.cs
// PURPOSE: CRC filter stream unit test
// 
// Copyright © 2013
// Brent M. Spell. All rights reserved.
//
// This library is free software; you can redistribute it and/or modify it 
// under the terms of the GNU Lesser General Public License as published 
// by the Free Software Foundation; either version 3 of the License, or 
// (at your option) any later version. This library is distributed in the 
// hope that it will be useful, but WITHOUT ANY WARRANTY; without even the 
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. 
// See the GNU Lesser General Public License for more details. You should 
// have received a copy of the GNU Lesser General Public License along with 
// this library; if not, write to 
//    Free Software Foundation, Inc. 
//    51 Franklin Street, Fifth Floor 
//    Boston, MA 02110-1301 USA
//===========================================================================
// System References
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
// Project References
using SkyFloe.IO;

namespace SkyFloe.Core.Test.IO
{
   [TestClass]
   public class TestCrcFilter
   {
      [TestMethod]
      public void TestConstruction ()
      {
         // invalid construction
         AssertException(() => new CrcFilter(null));
         // valid construction
         new CrcFilter(CreateStream(""));
         new CrcFilter(CreateStream("test"));
      }

      [TestMethod]
      public void TestOperations ()
      {
         // invalid CRC calculations
         AssertException(() => CrcFilter.Calculate(null, 0, 1));
         AssertException(() => CrcFilter.Calculate(new Byte[0], -1, 1));
         AssertException(() => CrcFilter.Calculate(new Byte[0], 1, 1));
         AssertException(() => CrcFilter.Calculate((Stream)null));
         AssertException(() => CrcFilter.Calculate((String)null));
         // full CRC calculations
         Assert.AreEqual(
            CrcFilter.Calculate(CreateData("")),
            CrcFilter.Calculate(CreateData(""))
         );
         Assert.AreEqual(
            CrcFilter.Calculate(CreateData("test")),
            CrcFilter.Calculate(CreateData("test"))
         );
         Assert.AreNotEqual(
            CrcFilter.Calculate(CreateData("test1")),
            CrcFilter.Calculate(CreateData("test2"))
         );
         Assert.AreEqual(
            CrcFilter.Calculate(CreateStream("")),
            CrcFilter.Calculate(CreateStream(""))
         );
         Assert.AreEqual(
            CrcFilter.Calculate(CreateStream("test")),
            CrcFilter.Calculate(CreateStream("test"))
         );
         Assert.AreNotEqual(
            CrcFilter.Calculate(CreateStream("test1")),
            CrcFilter.Calculate(CreateStream("test2"))
         );
         Assert.AreEqual(
            CrcFilter.Calculate(CreateData("")),
            CrcFilter.Calculate(CreateStream(""))
         );
         var tempFile = System.IO.Path.GetTempFileName();
         try
         {
            using (var temp = new FileStream(tempFile, FileMode.Open, FileAccess.Write))
               CreateStream("test").CopyTo(temp);
            Assert.AreEqual(
               CrcFilter.Calculate(CreateData("test")), 
               CrcFilter.Calculate(tempFile)
            );
         }
         finally
         {
            File.Delete(tempFile);
         }
         // incremental CRC calculations
         Assert.AreEqual(
            CrcFilter.CalculateFinal(CrcFilter.InitialValue),
            CrcFilter.Calculate(CreateStream(""))
         );
         Assert.AreEqual(
            CrcFilter.CalculateFinal(
               CrcFilter.CalculateIncremental(
                  CrcFilter.CalculateIncremental(
                     CrcFilter.InitialValue, 
                     CreateData("test"), 
                     0, 
                     2
                  ),
                  CreateData("test"),
                  2,
                  2
               )
            ),
            CrcFilter.Calculate(CreateData("test"))
         );
         // streaming calculations
         using (var crc = new CrcFilter(CreateStream("testread")))
         {
            new StreamReader(crc).ReadToEnd();
            Assert.AreEqual(crc.Value, CrcFilter.Calculate(CreateData("testread")));
         }
         using (var crc = new CrcFilter(CreateStream()))
         {
            CreateStream("testwrite").CopyTo(crc);
            Assert.AreEqual(crc.Value, CrcFilter.Calculate(CreateData("testwrite")));
         }
      }

      private Byte[] CreateData (String data)
      {
         return System.Text.Encoding.UTF8.GetBytes(data);
      }

      private Stream CreateStream (String data)
      {
         return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(data), false);
      }

      private Stream CreateStream ()
      {
         return new MemoryStream();
      }

      private void AssertException (Action a)
      {
         try { a(); }
         catch { return; }
         Assert.Fail("Expected: exception");
      }
   }
}
